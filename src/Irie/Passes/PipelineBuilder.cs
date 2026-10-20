using Irie.Target;

namespace Irie.Passes;

/// <summary>
/// The single definition of the Irie pass pipeline, shared by `iriec` and `dnrc`
/// so lit goldens and emulator-executed programs always exercise the same passes.
/// </summary>
/// <remarks>
/// The stages, in order (each pass documents itself; the comments below say why the
/// order matters):
///   1. Generic SSA clean-up and verification: GenericMirVerifierPass,
///      ArithSimplifyPass, InliningPass, RangeNarrowingPass, RangeFoldPass, ArithSimplifyPass (again, on the constants RangeFold made),
///      ConstantBranchFoldPass,
///      UnreachableBlockEliminationPass,
///      SinglePredecessorBlockParamsPass, HoistCommonCodePass,
///      ReturnMergePass (merges 2+ returns into one block; TailDuplicationPass undoes
///      it for trivial tails at the end).
///   2. Lowering to the target's shape: FrameLoweringPass, AbiLoweringPass
///      (parameters/returns become physreg copies via Target.CallLowering),
///      LegalizerPass (Target.LegalizerInfo), the target's pre-selection passes, and
///      InstructionSelectorPass (generic ops -&gt; the target's unpinned byte ops).
///   3. Placement (Target.CreateBeamPlacementPass): every value gets a register or a
///      home, block parameters disappear; the result is physreg-only MIR.
///   4. Post-placement: branch folding, CopyEliminationPass, the target's passes
///      (MOS6502: recursion saves, zero-page layout), frame access lowering,
///      prologue/epilogue insertion, CopyPropagationPass, PseudoExpansionPass, the
///      target's late optimisation, RegisterScavengingPass, the target's final passes
///      (MOS6502: block placement), a final verification, TailDuplicationPass.
///
/// Whole programs are emitted with Target.EmitProgram / BuildProgram, which also link
/// in the target's library routines; the pass pipeline itself does not.
/// </remarks>
public static class PipelineBuilder
{
    public static void AddStandardPipeline(
        PassManager passMgr, Target.Target target, PlacementTuning? tuning = null)
    {
        passMgr.AddPass(new GenericMirVerifierPass());
        passMgr.AddPass(new ArithSimplifyPass());
        // Inline before range analysis, so values are narrowed in the context of their caller.
        passMgr.AddPass(new InliningPass());
        passMgr.AddPass(new RangeNarrowingPass());
        passMgr.AddPass(new RangeFoldPass());
        // The compares RangeFold decided are constants now; fold what consumes them (`not` of a decided compare).
        passMgr.AddPass(new ArithSimplifyPass());
        passMgr.AddPass(new ConstantBranchFoldPass());
        passMgr.AddPass(new UnreachableBlockEliminationPass());
        passMgr.AddPass(new SinglePredecessorBlockParamsPass());
        passMgr.AddPass(new HoistCommonCodePass());
        passMgr.AddPass(new ReturnMergePass());
        passMgr.AddPass(new FrameLoweringPass());
        passMgr.AddPass(new AbiLoweringPass(target));
        passMgr.AddPass(new LegalizerPass(target.LegalizerInfo));
        // Target pre-isel passes (e.g. MOS6502 select-lowering) run on legalized,
        // still-SSA MIR — the llvm-mos slot for MOSLowerSelect.
        target.AddPreInstructionSelectionPasses(passMgr);
        // Shape selection: unpinned operations on typed vregs, then a placement that
        // gives every value a home. There are no register classes, no phi
        // elimination and no allocation: block parameters become writes of the
        // parameters' homes on each edge.
        passMgr.AddPass(new InstructionSelectorPass(target.CreateShapeSelector()));
        passMgr.AddPass(target.CreateBeamPlacementPass(tuning));
        passMgr.AddPass(new PostRegisterAllocationVerifierPass(target.RegisterInfo));
        // Placement mints frame slots for the values it spills; give them their
        // storage (the earlier FrameLowering ran before placement, so it never saw
        // them). Idempotent: slots that already have a global are skipped.
        passMgr.AddPass(new FrameLoweringPass());
        // Target branch-folding passes run on physreg-only MIR after placement and
        // before CopyElimination — the llvm-mos Control Flow Optimizer slot.
        // MOS6502 uses this for empty-block elimination.
        target.AddBranchFoldingPasses(passMgr);
        passMgr.AddPass(new CopyEliminationPass());
        target.AddPostPlacementPasses(passMgr);
        AddLoweringAndFinalPasses(passMgr, target);
    }

    /// <summary>Everything after the register / home assignment.</summary>
    private static void AddLoweringAndFinalPasses(PassManager passMgr, Target.Target target)
    {
        // Expand abstract frame accesses (mos6502.frame.*.byte) into concrete
        // addressing sequences per each slot's placement — the eliminateFrameIndex
        // analogue. Post-RA, after the target's post-RA passes, before PEI / pseudo
        // expansion.
        passMgr.AddPass(new FrameAccessLoweringPass(target.FrameLowering));
        passMgr.AddPass(new PrologueEpilogueInsertionPass(target.RegisterInfo, target.FrameLowering));
        // CopyPropagation — the llvm-mos `machine-cp` slot (after PEI, before pseudo
        // expansion). Cleans up redundant/dead physreg copies left by RA + the ABI +
        // PEI's prologue/epilogue shuttle, while every copy is still a uniform,
        // side-effect-free pseudo.copy. See CopyPropagationPass.
        passMgr.AddPass(new CopyPropagationPass(target.RegisterInfo));
        passMgr.AddPass(new PseudoExpansionPass(target.PseudoExpander));
        // Target late-optimization passes run on the final expanded physreg-only
        // stream — the llvm-mos `mos-late-opt` slot (after post-RA pseudo
        // expansion). MOS6502 uses this for the flag-from-load fold.
        target.AddPostPseudoExpansionPasses(passMgr);
        // Register scavenging runs LAST: PseudoExpansion mints copy-scratch vregs
        // (e.g. for immediate→zp moves), and this pass assigns each the cheapest GPR
        // dead at that point and drives the final lowering, leaving no vregs behind
        // (llvm-mos order: RA → pseudo expansion → scavenging).
        passMgr.AddPass(new RegisterScavengingPass(target.RegisterInfo, target.PseudoExpander));
        // Target final passes run after scavenging — the llvm-mos block-placement
        // slot. Block-placement drops fall-through jmps (making CFG edges implicit),
        // so it must run after scavenging, which still needs the explicit-jmp CFG to
        // compute physreg liveness.
        target.AddFinalPasses(passMgr);
        passMgr.AddPass(new PostRegisterAllocationVerifierPass(target.RegisterInfo, finalLowering: true));
        // Tail-duplication runs last — after the target's block-placement — to
        // rebalance trivial shared return tails left by ReturnMergePass (the
        // llvm-mos `tailduplication` analogue); it follows block-placement because
        // our block-placement has no integrated tail-dup. See TailDuplicationPass.
        passMgr.AddPass(new TailDuplicationPass(target.BranchLowering));
        target.AddPostTailDuplicationPasses(passMgr);
    }
}
