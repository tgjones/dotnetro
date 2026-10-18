using Irie.Mir;

namespace Irie.Target;

// Target hook for the passes that create or rewrite blocks *after* instruction
// selection. A fresh block needs an unconditional branch into its real target;
// a generic `cf.br` would never get lowered and would crash machine-code
// emission, so the target supplies its own jump (e.g. `mos6502.jmp.abs`).
//
// This mirrors LLVM's `TargetInstrInfo::insertUnconditionalBranch`, which
// `MachineBasicBlock::SplitCriticalEdge` calls for exactly this reason: the
// terminator of a block made on already-selected machine IR must be a target
// branch, not a generic one.
public abstract class BranchLowering
{
    // Emit an unconditional branch to `target` at the builder's current
    // insertion point (the end of a freshly-created split block).
    public abstract void InsertUnconditionalBranch(MirBuilder builder, BlockTarget target);

    // Recognize the target's unconditional branch. Returns true (and the
    // destination block) when `instr` is the post-isel unconditional jump this
    // lowering would emit (e.g. `mos6502.jmp.abs`). The inverse of
    // InsertUnconditionalBranch; the analogue of the unconditional-branch arm of
    // LLVM's TargetInstrInfo::analyzeBranch, used by TailDuplicationPass to find
    // the redirectable edges into a tail.
    public abstract bool TryGetUnconditionalBranchTarget(MirInstruction instr, out MirBlock target);
}
