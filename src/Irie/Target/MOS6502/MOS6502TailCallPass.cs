using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

/// <summary>
/// Tail-call folding: a `jsr.abs` immediately followed by `rts` becomes
/// `jmp.abs`, so the callee's own `rts` returns to our caller (llvm-mos reaches
/// the same code by lowering a call in tail position to a jump in ISel; here it is
/// a peephole on the final stream, which also catches the `jsr; rts` pairs that
/// ReturnMerge / TailDuplication leave adjacent).
/// </summary>
/// <remarks>
/// Adjacency is the whole safety condition: any epilogue work (callee-saved
/// restores, a stack-pointer fix-up) sits between the call and the `rts` and
/// blocks the fold. The `jsr`'s implicit argument uses and clobber defs are
/// dropped with it — the verifier has already run and nothing reads them after.
/// </remarks>
public sealed class MOS6502TailCallPass : MirFunctionPass
{
    public override string Name => "MOS6502TailCall";

    public override void Run(MirFunction function)
    {
        var jsr = MOS6502Dialect.OpRef(MOS6502Op.JsrAbs);
        var jmp = MOS6502Dialect.OpRef(MOS6502Op.JmpAbs);
        var rts = MOS6502Dialect.OpRef(MOS6502Op.Rts);

        foreach (var block in function.Blocks)
        {
            var instructions = block.Instructions;
            for (var i = 0; i + 1 < instructions.Count; i++)
            {
                if (instructions[i].Opcode != jsr || instructions[i + 1].Opcode != rts)
                    continue;

                instructions[i] = new MirInstruction(jmp, [instructions[i].Operands[0]]);
                instructions.RemoveAt(i + 1);
            }
        }
    }
}
