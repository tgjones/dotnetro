using Irie.Dialects.Arith;
using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

/// <summary>
/// Replaces an <c>arith.cmpi</c> whose outcome the operand value ranges fix with an <c>i1</c> constant, on
/// generic SSA MIR (Oscar64's <c>SimplifyIntegerRangeRelops</c>).
/// </summary>
/// <remarks>
/// A byte zero-extended to <c>int</c> and tested against 256, or a loop counter tested against a bound the loop
/// provably never reaches, is decided before the program runs. The compare becomes a constant;
/// <see cref="ConstantBranchFoldPass"/> then folds a branch on it and
/// <see cref="UnreachableBlockEliminationPass"/> removes the dead arm.
///
/// The ranges come from <see cref="ValueRangeAnalysis"/>, which this pass recomputes; the call in <see cref="Run"/>
/// is the one place to change when analysis results can be shared between passes. The ranges are computed once,
/// before any fold, so a compare that only becomes decidable after a dead edge is removed (it tightens a block
/// parameter's range) is not folded by this run. A decided compare does count as a constant for the compares
/// after it.
///
/// Runs after <see cref="RangeNarrowingPass"/> and before <see cref="SinglePredecessorBlockParamsPass"/>, which
/// erases the block parameters that carry per-edge ranges.
/// </remarks>
public sealed class RangeFoldPass : MirFunctionPass
{
    public override string Name => "RangeFold";

    public override void Run(MirFunction function)
    {
        var ranges = ValueRangeAnalysis.Compute(function);
        var builder = new MirBuilder(function);
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions.ToArray())
            {
                if (instruction.Opcode != ArithDialect.OpRef(ArithOp.CmpI)
                    || instruction.Operands is not [VirtualReg { IsDefinition: true } result, Immediate predicate,
                                                    VirtualReg a, VirtualReg b]
                    || RangeOf(function, ranges, a.Id) is not { } aRange
                    || RangeOf(function, ranges, b.Id) is not { } bRange
                    || aRange.TryDecide((ArithCmpPredicate)predicate.Value, bRange) is not { } outcome)
                    continue;

                builder.SetInsertionPointBefore(instruction);
                var constant = builder.BuildConstant(IRType.I1, outcome ? 1 : 0);
                function.ReplaceAllUsesOfRegister(result.Id, constant);
                // A compare of this result (the front end's `(a < b) == false`) is decided in turn.
                ranges[constant] = ValueRange.Constant(outcome ? 1 : 0);
                builder.Remove(instruction);
            }
        }
    }

    /// <summary>
    /// The range of an integer vreg; the analysis leaves out a vreg whose range is the full range of its type.
    /// </summary>
    private static ValueRange? RangeOf(MirFunction function, Dictionary<int, ValueRange> ranges, int vreg)
    {
        if (ranges.TryGetValue(vreg, out var range))
            return range;
        return function.GetVRegAnnotation(vreg) is TypedVReg { Type: IntegerType { SizeInBits: var bits } }
            ? ValueRange.Full(bits)
            : null;
    }
}
