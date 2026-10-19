using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

/// <summary>
/// Shrinks integer arithmetic on generic SSA MIR to the bytes its result needs, using the value ranges of
/// <see cref="ValueRangeAnalysis"/>, and annotates every integer vreg with its range
/// (<c>%5 : i32 in [0, 766] = ...</c>).
/// </summary>
/// <remarks>
/// C# promotes everything to <c>int</c>, so the front end emits <c>zext i8 → i32</c> and 32-bit arithmetic on
/// values that need one or two bytes; the legalizer would split each such operation into four byte
/// operations. When the range of <c>r = op_N(a, b)</c> fits an unsigned M-bit value (M a smaller type width),
/// the operation is rewritten to <c>r = zext(op_M(trunc a, trunc b))</c>. This is exact: add, sub, mul, shl and
/// the bitwise operations compute the low M bits of the result from the low M bits of the operands (modular
/// arithmetic), and the range says the high bits of the result are zero. The legalizer then splits the narrow
/// operation, and the zero extension is constant-0 bytes that fold away.
///
/// A peephole cleans up the extensions and truncations this leaves: <c>trunc(zext x)</c> and
/// <c>trunc(sext x)</c> become <c>x</c> or a narrower extension or truncation of it, a truncated constant is a
/// narrower constant, and <c>zext(trunc y)</c> is <c>y</c> when the range of y fits the narrow width. Dead
/// arithmetic and casts are then removed.
///
/// Only results with a non-negative range are narrowed. Narrowing to a signed range would need a sign
/// extension for every full-width consumer, which costs more than it saves unless the consumers narrow too.
///
/// Runs after <see cref="ArithSimplifyPass"/> (constants are canonical) and before
/// <see cref="SinglePredecessorBlockParamsPass"/>, which erases the block parameters that carry per-edge
/// ranges. Constants are not annotated: their range is their value, which their definition already prints.
/// </remarks>
public sealed class RangeNarrowingPass : MirFunctionPass
{
    public override string Name => "RangeNarrowing";

    public override void Run(MirFunction function)
    {
        var ranges = ValueRangeAnalysis.Compute(function);
        var builder = new MirBuilder(function);

        // Annotate first: the rewrites below keep each result's vreg, so its range stays valid.
        var constants = new HashSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Opcode == ArithDialect.OpRef(ArithOp.Constant)
                    && instruction.Operands[0] is VirtualReg def)
                    constants.Add(def.Id);
        foreach (var (vreg, range) in ranges)
            if (!constants.Contains(vreg))
                function.SetValueRange(vreg, range);

        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions.ToArray())
                NarrowArithmetic(function, builder, instruction, ranges);

        CleanUpCasts(function, builder, ranges);
        RemoveDeadCode(function, builder);
    }

    /// <summary>
    /// <c>r = op_N(a, b)</c> with the range of r fitting an unsigned M-bit value becomes
    /// <c>r = zext(op_M(trunc a, trunc b))</c>.
    /// </summary>
    private static void NarrowArithmetic(
        MirFunction function, MirBuilder builder, MirInstruction instruction, Dictionary<int, ValueRange> ranges)
    {
        if (instruction.Opcode.Dialect != ArithDialect.Id
            || (ArithOp)instruction.Opcode.Code is not (ArithOp.AddI or ArithOp.SubI or ArithOp.MulI
                or ArithOp.And or ArithOp.Or or ArithOp.Xor or ArithOp.Shl)
            || instruction.Operands is not [VirtualReg { IsDefinition: true } result, VirtualReg a, VirtualReg b])
            return;

        if (function.GetVRegAnnotation(result.Id) is not TypedVReg { Type: IntegerType { SizeInBits: var bits } }
            || !ranges.TryGetValue(result.Id, out var range)
            || NarrowWidth(range, bits) is not { } narrow)
            return;

        var op = (ArithOp)instruction.Opcode.Code;
        var narrowType = IntegerTypeOfWidth(narrow);
        builder.SetInsertionPointBefore(instruction);

        MirOperand narrowB;
        if (op == ArithOp.Shl)
        {
            // The shift amount is an i8 constant and stays as it is; it must be a valid amount for the narrow type.
            if (!ranges.TryGetValue(b.Id, out var amount) || amount.Lo != amount.Hi || amount.Lo >= narrow)
                return;
            narrowB = new VirtualReg(b.Id, IsDefinition: false);
        }
        else
        {
            narrowB = new VirtualReg(BuildTrunc(function, builder, b.Id, narrowType), IsDefinition: false);
        }

        var narrowResult = function.CreateVirtualRegister(narrowType);
        var narrowA = BuildTrunc(function, builder, a.Id, narrowType);
        builder.BuildInstruction(instruction.Opcode,
            new VirtualReg(narrowResult, IsDefinition: true),
            new VirtualReg(narrowA, IsDefinition: false),
            narrowB);
        builder.BuildInstruction(CastDialect.OpRef(CastOp.Zext),
            new VirtualReg(result.Id, IsDefinition: true),
            new VirtualReg(narrowResult, IsDefinition: false));
        builder.Remove(instruction);
    }

    /// <summary>
    /// The smallest type width, in bits, below <paramref name="bits"/> that holds every value of a non-negative
    /// range; null when the range is negative or needs the full width.
    /// </summary>
    private static int? NarrowWidth(ValueRange range, int bits)
    {
        if (range.Lo < 0)
            return null;
        foreach (var width in new[] { 8, 16, 32 })
            if (width < bits && range.FitsUnsigned(width))
                return width;
        return null;
    }

    private static IntegerType IntegerTypeOfWidth(int bits) => bits switch
    {
        8 => IRType.I8,
        16 => IRType.I16,
        32 => IRType.I32,
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "No integer type of this width."),
    };

    private static int BuildTrunc(MirFunction function, MirBuilder builder, int source, IRType type)
    {
        var result = function.CreateVirtualRegister(type);
        builder.BuildInstruction(CastDialect.OpRef(CastOp.Trunc),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(source, IsDefinition: false));
        return result;
    }

    /// <summary>
    /// The peephole over <c>cast.trunc</c>, <c>cast.zext</c> and <c>cast.sext</c>, repeated until nothing
    /// changes (a rewrite exposes the next one).
    /// </summary>
    private static void CleanUpCasts(MirFunction function, MirBuilder builder, Dictionary<int, ValueRange> ranges)
    {
        bool changed;
        do
        {
            changed = false;
            var definitions = new Dictionary<int, MirInstruction>();
            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions)
                    foreach (var operand in instruction.Operands)
                        if (operand is VirtualReg { IsDefinition: true } def)
                            definitions[def.Id] = instruction;

            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions.ToArray())
                    if (instruction.Parent is not null
                        && instruction.Opcode.Dialect == CastDialect.Id
                        && CleanUpCast(function, builder, instruction, definitions, ranges))
                        changed = true;

            changed |= MergeDuplicateCasts(function, builder);
        } while (changed);
    }

    /// <summary>
    /// Within a block, a cast of the same source to the same type as an earlier cast is the earlier one: each
    /// narrowed operation truncates its own copy of an operand that several operations share.
    /// </summary>
    private static bool MergeDuplicateCasts(MirFunction function, MirBuilder builder)
    {
        var merged = false;
        foreach (var block in function.Blocks)
        {
            var seen = new Dictionary<(OpcodeRef Opcode, int Source, IRType Type), int>();
            foreach (var instruction in block.Instructions.ToArray())
            {
                if (instruction.Opcode.Dialect != CastDialect.Id
                    || instruction.Operands is not [VirtualReg { IsDefinition: true } result, VirtualReg source]
                    || function.GetVRegAnnotation(result.Id) is not TypedVReg { Type: var type })
                    continue;

                if (seen.TryGetValue((instruction.Opcode, source.Id, type), out var earlier))
                {
                    Replace(function, builder, instruction, result.Id, earlier);
                    merged = true;
                }
                else
                {
                    seen[(instruction.Opcode, source.Id, type)] = result.Id;
                }
            }
        }
        return merged;
    }

    private static bool CleanUpCast(
        MirFunction function,
        MirBuilder builder,
        MirInstruction instruction,
        Dictionary<int, MirInstruction> definitions,
        Dictionary<int, ValueRange> ranges)
    {
        if (instruction.Operands is not [VirtualReg { IsDefinition: true } result, VirtualReg source]
            || function.GetVRegAnnotation(result.Id) is not TypedVReg { Type: IntegerType resultType }
            || function.GetVRegAnnotation(source.Id) is not TypedVReg { Type: IntegerType })
            return false;

        var op = (CastOp)instruction.Opcode.Code;
        definitions.TryGetValue(source.Id, out var sourceDef);
        if (sourceDef?.Parent is null)
            sourceDef = null;

        switch (op)
        {
            case CastOp.Trunc when sourceDef is { Operands: [_, Immediate value] }
                                   && sourceDef.Opcode == ArithDialect.OpRef(ArithOp.Constant):
            {
                builder.SetInsertionPointBefore(instruction);
                var mask = resultType.SizeInBits >= 64 ? -1L : (1L << resultType.SizeInBits) - 1;
                var constant = builder.BuildConstant(resultType, value.Value & mask);
                Replace(function, builder, instruction, result.Id, constant);
                return true;
            }

            case CastOp.Trunc when sourceDef is { Operands: [_, VirtualReg inner] } extension
                                   && extension.Opcode.Dialect == CastDialect.Id
                                   && (CastOp)extension.Opcode.Code is CastOp.Zext or CastOp.Sext
                                   && function.GetVRegAnnotation(inner.Id) is TypedVReg { Type: IntegerType innerType }:
            {
                // trunc(ext x): x itself, a narrower truncation of x, or a narrower extension of it.
                var extOp = (CastOp)extension.Opcode.Code;
                if (innerType.SizeInBits == resultType.SizeInBits)
                {
                    Replace(function, builder, instruction, result.Id, inner.Id);
                    return true;
                }

                builder.SetInsertionPointBefore(instruction);
                builder.BuildInstruction(
                    innerType.SizeInBits > resultType.SizeInBits ? CastDialect.OpRef(CastOp.Trunc) : CastDialect.OpRef(extOp),
                    new VirtualReg(result.Id, IsDefinition: true),
                    new VirtualReg(inner.Id, IsDefinition: false));
                builder.Remove(instruction);
                return true;
            }

            case CastOp.Zext or CastOp.Sext when sourceDef is { Operands: [_, VirtualReg wide] } truncation
                                                 && truncation.Opcode == CastDialect.OpRef(CastOp.Trunc)
                                                 && function.GetVRegAnnotation(wide.Id) is TypedVReg { Type: IntegerType wideType }
                                                 && wideType == resultType
                                                 && ranges.TryGetValue(wide.Id, out var wideRange):
            {
                // ext(trunc y) is y when the range of y fits the narrow width.
                var narrowBits = ((IntegerType)((TypedVReg)function.GetVRegAnnotation(source.Id)).Type).SizeInBits;
                var fits = op == CastOp.Zext ? wideRange.FitsUnsigned(narrowBits) : wideRange.FitsSigned(narrowBits);
                if (!fits)
                    return false;
                Replace(function, builder, instruction, result.Id, wide.Id);
                return true;
            }
        }

        return false;
    }

    private static void Replace(MirFunction function, MirBuilder builder, MirInstruction instruction, int oldVreg, int newVreg)
    {
        function.ReplaceAllUsesOfRegister(oldVreg, newVreg);
        builder.Remove(instruction);
    }

    private static void RemoveDeadCode(MirFunction function, MirBuilder builder)
    {
        bool removedAny;
        do
        {
            removedAny = false;
            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions.ToArray())
                    if (instruction.Parent is not null
                        && (instruction.Opcode.Dialect == ArithDialect.Id || instruction.Opcode.Dialect == CastDialect.Id)
                        && function.IsTriviallyDead(instruction))
                    {
                        builder.Remove(instruction);
                        removedAny = true;
                    }
        } while (removedAny);
    }
}
