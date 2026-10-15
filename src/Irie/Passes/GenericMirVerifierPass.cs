using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Cast;
using Irie.Dialects.Mem;
using Irie.Mir;

namespace Irie.Passes;

// Pre-pipeline verifier for generic-dialect MIR. Enforces the invariant that an
// integer *value* is never an inline `Immediate` operand — it is always an
// `arith.constant` def. Inline `Immediate`s are reserved for structural
// attributes (the cmpi predicate, mem offsets/counts, frame-slot indices, …)
// and for `arith.constant`'s own value. Each dialect declares its legal
// immediate operand positions via Dialect.IsLegalImmediateOperand.
//
// It also type-checks pointers, which are a distinct `ptr` type rather than an
// integer: address operands (mem.load/store/fill, the call.indirect target) must
// be `ptr`; mem.symbol / mem.frame_addr / mem.ptradd / mem.load.ptr define `ptr`;
// mem.ptradd is `(ptr, i16) -> ptr`; cast.ptrtoint / cast.inttoptr convert
// between `ptr` and `i16`; no arith op takes or produces a `ptr`; and the
// arguments and results of a call.func to a function in the module match that
// function's declared signature (catching drift between the C# runtime,
// runtime.irie and translator-built calls).
//
// Mutates nothing; throws MirVerificationException on the first violation so a
// bad inline literal or mistyped pointer surfaces as a clean diagnostic rather
// than an InvalidCast crash deeper in isel/lowering. Runs first, before
// ReturnMergePass.
public sealed class GenericMirVerifierPass : Pass
{
    public override string Name => "GenericMirVerifier";

    public override void Run(CompilationContext context)
    {
        foreach (var function in context.Module.Functions)
            Run(function, context.Module);
    }

    // Verifies one function without module context: the call-signature rule is
    // skipped, as no callee can be looked up.
    public void Run(MirFunction function) => Run(function, module: null);

    // The invariant only applies to the generic (non-target) dialects — the ones
    // MirBootstrap registers. Target dialects (e.g. mos6502) legitimately carry
    // inline immediates (e.g. `mos6502.lda.imm #$00`), so the verifier ignores
    // any instruction whose dialect is not in this set. The set is keyed by the
    // dialects' stable prefixes rather than IDs (IDs are assigned at registration
    // time, which differs depending on which targets have been constructed).
    private static readonly HashSet<string> GenericDialectPrefixes =
        ["core", "arith", "cast", "cf", "call", "mem", "pseudo"];

    private void Run(MirFunction function, MirModule? module)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var dialect = DialectRegistry.ById(instr.Opcode.Dialect);

                // Only the generic dialects participate in the inline-immediate
                // invariant; target dialects carry legal inline immediates.
                if (!GenericDialectPrefixes.Contains(dialect.Prefix))
                    continue;

                VerifyTypes(function, module, instr);

                // Defs always come first in the operand array; count the leading
                // register defs so we can map array indices to use indices.
                var defCount = 0;
                foreach (var operand in instr.Operands)
                {
                    if (operand is VirtualReg { IsDefinition: true }
                        or PhysicalReg { IsDefinition: true })
                        defCount++;
                    else
                        break;
                }

                for (var i = 0; i < instr.Operands.Length; i++)
                {
                    if (instr.Operands[i] is not Immediate imm) continue;

                    var useIndex = i - defCount;
                    if (dialect.IsLegalImmediateOperand(instr.Opcode.Code, useIndex))
                        continue;

                    var opName = $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";
                    throw new MirVerificationException(
                        $"MirVerifier: in @{function.Name}, {opName} has an inline immediate " +
                        $"value operand ({imm.Value}) at use {useIndex}. Value constants must be " +
                        $"an arith.constant def: `%c = arith.constant {imm.Value}` then " +
                        $"`arith.addi %x, %c`. Inline immediates are only allowed for structural " +
                        $"attributes (e.g. the cmpi predicate) and arith.constant's own value.");
                }
            }
        }
    }

    // ---- pointer typing -------------------------------------------------------

    private static void VerifyTypes(MirFunction function, MirModule? module, MirInstruction instr)
    {
        var defs = new List<VirtualReg>();
        var uses = new List<MirOperand>();
        foreach (var operand in instr.Operands)
        {
            if (operand is VirtualReg { IsDefinition: true } def) defs.Add(def);
            else uses.Add(operand);
        }

        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        var opName = $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";

        void Expect(MirOperand? operand, string role, Func<IRType, bool> accept, string expected)
        {
            if (operand is not VirtualReg reg
                || !function.TryGetVRegAnnotation(reg.Id, out var annotation)
                || annotation is not TypedVReg typed)
                return;
            if (!accept(typed.Type))
                throw new MirVerificationException(
                    $"MirVerifier: in @{function.Name}, {opName} {role} %{reg.Id} has type " +
                    $"{typed.Type.DisplayName}, expected {expected}.");
        }

        void ExpectPtr(MirOperand? operand, string role) =>
            Expect(operand, role, t => t is PointerType, "ptr");
        void ExpectI16(MirOperand? operand, string role) =>
            Expect(operand, role, t => t.Equals(IRType.I16), "i16");

        if (instr.Opcode.Dialect == MemDialect.Id)
        {
            switch ((MemOp)instr.Opcode.Code)
            {
                case MemOp.Symbol:
                case MemOp.FrameAddr:
                    ExpectPtr(defs.FirstOrDefault(), "result");
                    break;
                case MemOp.LoadI8 or MemOp.LoadI16 or MemOp.LoadI32 or MemOp.LoadI64:
                    ExpectPtr(uses.FirstOrDefault(), "address");
                    break;
                case MemOp.LoadPtr:
                    ExpectPtr(defs.FirstOrDefault(), "result");
                    ExpectPtr(uses.FirstOrDefault(), "address");
                    break;
                case MemOp.StoreI8 or MemOp.StoreI16 or MemOp.StoreI32 or MemOp.StoreI64 or MemOp.Fill:
                    ExpectPtr(uses.FirstOrDefault(), "address");
                    break;
                case MemOp.StorePtr:
                    ExpectPtr(uses.FirstOrDefault(), "address");
                    ExpectPtr(uses.Skip(1).FirstOrDefault(), "stored value");
                    break;
                case MemOp.PtrAdd:
                    ExpectPtr(defs.FirstOrDefault(), "result");
                    ExpectPtr(uses.FirstOrDefault(), "base");
                    ExpectI16(uses.Skip(1).FirstOrDefault(), "offset");
                    break;
            }
        }
        else if (instr.Opcode.Dialect == CastDialect.Id)
        {
            switch ((CastOp)instr.Opcode.Code)
            {
                case CastOp.PtrToInt:
                    ExpectI16(defs.FirstOrDefault(), "result");
                    ExpectPtr(uses.FirstOrDefault(), "operand");
                    break;
                case CastOp.IntToPtr:
                    ExpectPtr(defs.FirstOrDefault(), "result");
                    ExpectI16(uses.FirstOrDefault(), "operand");
                    break;
            }
        }
        else if (instr.Opcode.Dialect == ArithDialect.Id)
        {
            foreach (var def in defs)
                Expect(def, "result", t => t is not PointerType, "a non-pointer type (arith ops are integer-only)");
            foreach (var use in uses)
                Expect(use, "operand", t => t is not PointerType, "a non-pointer type (arith ops are integer-only)");
        }
        else if (instr.Opcode.Dialect == CallDialect.Id)
        {
            switch ((CallOp)instr.Opcode.Code)
            {
                case CallOp.Indirect:
                    ExpectPtr(uses.FirstOrDefault(), "target");
                    break;
                case CallOp.Func:
                    VerifyCallSignature(function, module, instr, defs, uses, opName);
                    break;
            }
        }
    }

    // call.func @callee, args... against the callee's declared signature, when the
    // callee is a function in the module (a symbol defined elsewhere has no
    // signature to check against).
    private static void VerifyCallSignature(
        MirFunction function, MirModule? module, MirInstruction instr,
        List<VirtualReg> defs, List<MirOperand> uses, string opName)
    {
        if (module is null || uses.FirstOrDefault() is not Symbol callee)
            return;
        var target = module.Functions.FirstOrDefault(f => f.Name == callee.Name);
        if (target is null)
            return;

        var args = uses.Skip(1).Where(u => u is not Immediate).ToList(); // an Immediate is the calling convention
        if (args.Count != target.ParameterTypes.Length)
            throw new MirVerificationException(
                $"MirVerifier: in @{function.Name}, {opName} @{callee.Name} passes {args.Count} " +
                $"argument(s) but the callee takes {target.ParameterTypes.Length}.");

        for (var i = 0; i < args.Count; i++)
            CheckCallType(function, opName, callee.Name, args[i], target.ParameterTypes[i], $"argument {i}");

        var expectedResults = target.ReturnType is VoidType ? 0 : 1;
        if (defs.Count != expectedResults)
            throw new MirVerificationException(
                $"MirVerifier: in @{function.Name}, {opName} @{callee.Name} defines {defs.Count} " +
                $"result(s) but the callee returns {target.ReturnType.DisplayName}.");
        if (defs.Count == 1)
            CheckCallType(function, opName, callee.Name, defs[0], target.ReturnType, "result");
    }

    private static void CheckCallType(
        MirFunction function, string opName, string callee, MirOperand operand, IRType expected, string role)
    {
        if (operand is not VirtualReg reg
            || !function.TryGetVRegAnnotation(reg.Id, out var annotation)
            || annotation is not TypedVReg typed)
            return;
        if (!typed.Type.Equals(expected))
            throw new MirVerificationException(
                $"MirVerifier: in @{function.Name}, {opName} @{callee} {role} %{reg.Id} has type " +
                $"{typed.Type.DisplayName}, but the callee's signature declares {expected.DisplayName}.");
    }
}
