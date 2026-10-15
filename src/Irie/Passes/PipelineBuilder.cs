using Irie.Target;

namespace Irie.Passes;

// The single definition of the Irie pass pipeline, shared by `iriec` and `dnrc`
// so lit goldens and emulator-executed programs always exercise the same passes.
public static class PipelineBuilder
{
    public static void AddStandardPipeline(PassManager passMgr, Target.Target target)
    {
        passMgr.AddPass(new GenericMirVerifierPass());
        passMgr.AddPass(new ArithSimplifyPass());
        passMgr.AddPass(new HoistCommonCodePass());
        passMgr.AddPass(new ReturnMergePass());
        passMgr.AddPass(new FrameLoweringPass());
        passMgr.AddPass(new AbiLoweringPass(target));
        passMgr.AddPass(new LegalizerPass(target.LegalizerInfo));
        // Target pre-isel passes (e.g. MOS6502 select-lowering) run on legalized,
        // still-SSA MIR — the llvm-mos slot for MOSLowerSelect.
        target.AddPreInstructionSelectionPasses(passMgr);
        passMgr.AddPass(new InstructionSelectorPass(target.InstructionSelector));
        // Insert class-crossing legalization copies wherever a use operand's required
        // register class is disjoint from its source vreg's def class (e.g. an `ac`
        // result feeding an `imag8`/zp adc addend) — the llvm-mos ISel copy that gives
        // every vreg one consistent class so RA never sees an `Ac ∩ Imag8 = ∅` vreg.
        // A verified no-op while isel's out-funnels relocate every forced-`$a` result
        // into the flexible Anyi8 class (⊇ Imag8): no operand is class-disjoint, so
        // zero copies are inserted on every existing function. Runs right after isel,
        // before the other copy-inserting passes and RA. See OperandLegalizationPass.
        passMgr.AddPass(new OperandLegalizationPass(target.RegisterInfo));
        // Conventional SSA destruction up front, then the allocator chain.
        passMgr.AddPass(new PhiEliminationPass(target.BranchLowering));
        passMgr.AddPass(new TwoAddressInstructionPass());
        // Sink rematerializable defs (constants, symbol bytes) into their sole
        // use block before allocation — the llvm-mos `machine-sink` analogue.
        // Relieves entry-block pressure so long-lived values (arguments) keep the
        // scarce real registers instead of being evicted to zero page. See
        // RematerializableSinkPass.
        passMgr.AddPass(new RematerializableSinkPass());
        passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
        passMgr.AddPass(new PostRegisterAllocationVerifierPass(target.RegisterInfo));
        // RA mints frame slots for the values it spills; give them their storage
        // (the earlier FrameLowering ran before RA, so it never saw them). Idempotent:
        // slots that already have a global are skipped.
        passMgr.AddPass(new FrameLoweringPass());
        // Target branch-folding passes run on physreg-only MIR after RA and before
        // CopyElimination — the llvm-mos Control Flow Optimizer slot (after the VReg
        // rewriter, before copy-opt). MOS6502 uses this for empty-block elimination.
        target.AddBranchFoldingPasses(passMgr);
        passMgr.AddPass(new CopyEliminationPass());
        target.AddPostRegisterAllocationPasses(passMgr);
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
    }
}
