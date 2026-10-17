using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// PrologueEpilogueInsertionPass — inserts callee-saved register save/restore
// sequences in each function's prologue/epilogue. Mirrors LLVM's
// PrologEpilogInserter.
// =============================================================================
//
// The framework is target-independent: this pass determines which callee-saved
// registers a function actually clobbers, finds the entry block and the return
// instructions, and drives the target's FrameLowering hooks to emit the actual
// save/restore instructions. Only the instruction emission (and the target's
// notion of "the return instruction") is target-specific.
//
// Pipeline position: after register allocation and any target post-RA passes,
// before PseudoExpansionPass — at which point physreg assignments and the set of
// defined registers are final (matching LLVM's "PEI after RA, before post-RA
// pseudo expansion"). The concrete target ops it emits (e.g. lda.zp/pha/sta.zp)
// are already final machine ops, so the later pseudo-expansion / scavenging
// passes (which only touch pseudo.*) leave them alone.
//
// A no-op for MOS6502 apart from the soft-stack reserve: no register is
// callee-saved there (values that survive calls live in homes that program layout
// keeps disjoint from every callee's).
public sealed class PrologueEpilogueInsertionPass(
    TargetRegisterInfo registerInfo, FrameLowering frameLowering) : MirFunctionPass
{
    public override string Name => "PrologueEpilogueInsertion";

    public override void Run(MirFunction function)
    {
        if (function.Blocks.Count == 0)
            return;

        var builder = new MirBuilder(function);

        // Snapshot the returns first: the emitters below mutate the blocks'
        // instruction lists.
        var returns = new List<MirInstruction>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                if (frameLowering.IsReturnInstruction(instr))
                    returns.Add(instr);

        // The callee-saved registers this function actually clobbers: the target's
        // callee-saved list ∩ the physregs that appear as a definition anywhere in
        // the function (including inside surviving pseudo.copy defs). Ascending,
        // deduplicated.
        var calleeSaved = registerInfo.GetCalleeSavedRegisters();
        var definedCalleeSaved = new SortedSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg { IsDefinition: true } p && ContainsReg(calleeSaved, p.Id))
                        definedCalleeSaved.Add(p.Id);

        var saved = definedCalleeSaved.ToList();
        var softStackBytes = frameLowering.GetSoftStackFrameBytes(function);

        if (saved.Count > 0)
        {
            frameLowering.EmitCalleeSavedSpills(function.Blocks[0], saved, builder);
            foreach (var returnInstr in returns)
                frameLowering.EmitCalleeSavedRestores(returnInstr, saved, builder);
        }

        if (softStackBytes > 0)
        {
            frameLowering.EmitSoftStackReserve(function.Blocks[0], softStackBytes, builder);
            foreach (var returnInstr in returns)
                frameLowering.EmitSoftStackRelease(returnInstr, softStackBytes, builder);
        }
    }

    private static bool ContainsReg(ReadOnlySpan<int> regs, int reg)
    {
        foreach (var r in regs)
            if (r == reg)
                return true;
        return false;
    }
}
