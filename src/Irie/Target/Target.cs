using Irie.Mir;

namespace Irie.Target;

// Top-of-tree target abstraction for the unified MIR pipeline. Targets
// register their dialect with DialectRegistry from their constructor and
// expose the lowering hooks the generic passes need.
public abstract class Target
{
    public abstract Dialect Dialect { get; }
    public abstract CallLowering CallLowering { get; }

    // The lowering for a calling convention named by a call site
    // (Irie.Dialects.Call.CallConvention). Default is `CallLowering`; a target
    // with extra conventions overrides this.
    public virtual CallLowering GetCallLowering(int convention) =>
        convention == Irie.Dialects.Call.CallConvention.Default
            ? CallLowering
            : throw new NotSupportedException($"Calling convention {convention} is not supported by this target.");
    public abstract LegalizerInfo LegalizerInfo { get; }
    public abstract PseudoExpander PseudoExpander { get; }
    public abstract BranchLowering BranchLowering { get; }
    public abstract FrameLowering FrameLowering { get; }

    // Emits a MachineCodeModule from a post-PseudoExpansion MirModule. Called
    // by the driver after passMgr.Run(); not part of the pass pipeline.
    public abstract MachineCodeEmitter MachineCodeEmitter { get; }

    // Allocatable-register / class metadata for the unified-MIR register
    // allocator. The same TargetRegisterInfo type used by the legacy CodeGen
    // pipeline.
    public abstract TargetRegisterInfo RegisterInfo { get; }

    // Display name for a physical-register ID. Used by MirWriter to print
    // `$A` instead of `$0`, etc.
    public abstract string GetRegisterName(int physReg);

    // Called by the driver between LegalizerPass and InstructionSelectorPass.
    // Targets append passes that run on legalized, still-SSA MIR before isel —
    // e.g. MOS6502's select-lowering, which expands materialized arith.select into
    // a cf.cond_br diamond (the llvm-mos MOSLowerSelect analogue, which likewise
    // runs after the legalizer and before instruction selection).
    public virtual void AddPreInstructionSelectionPasses(Irie.Passes.PassManager pm) { }

    // Shape selection and the placements (Irie.Passes.PlacementStrategy).
    public abstract Irie.Target.InstructionSelector CreateShapeSelector();

    public abstract Irie.Passes.Pass CreateNaivePlacementPass();

    public abstract Irie.Passes.Pass CreateBeamPlacementPass();

    // Called by the driver after placement, between CopyEliminationPass and the
    // lowering passes: the target's zero-page layout and the like.
    public virtual void AddPostPlacementPasses(Irie.Passes.PassManager pm) { }

    // Called by the driver between placement and CopyEliminationPass.
    // Targets append their branch-folder passes here — the llvm-mos Control Flow
    // Optimizer slot, which runs after the VReg rewriter and before copy-opt
    // (Irie's CopyElimination). MOS6502 uses this for empty-block elimination.
    public virtual void AddBranchFoldingPasses(Irie.Passes.PassManager pm) { }

    // Called by iriec after PseudoExpansionPass (and before RegisterScavenging).
    // Targets append late-optimization passes that need the final expanded
    // physreg-only stream — e.g. MOS6502's flag-from-load fold, the llvm-mos
    // `mos-late-opt` analogue, which likewise runs after post-RA pseudo
    // expansion.
    public virtual void AddPostPseudoExpansionPasses(Irie.Passes.PassManager pm) { }

    // Called by the driver as the very last stage, AFTER RegisterScavenging.
    // Targets append final block-layout passes here — the llvm-mos
    // block-placement slot (the last codegen pass before emission). It must run
    // after scavenging because block-placement drops the explicit unconditional
    // jmp on a fall-through edge, turning that edge implicit; RegisterScavenging
    // (which derives physreg liveness from the explicit-terminator CFG via
    // MirBlock.LiveIns / Successors) must therefore have already run.
    public virtual void AddFinalPasses(Irie.Passes.PassManager pm) { }

    // Called after tail-duplication, the last generic pass: it can leave a call
    // directly followed by a return (the duplicated return tail), which a target
    // may fold into a tail jump.
    public virtual void AddPostTailDuplicationPasses(Irie.Passes.PassManager pm) { }

    // Hand-written MIR runtime for this target (OS-call wrappers, `start`, the
    // indirect-call trampoline, ...), as MIR text. The IL→MIR translator parses it
    // and merges its functions into the module it produces.
    public abstract string GetRuntime();

    // The target's library of hand-written machine-code routines (the libcalls
    // its legalizer emits calls to), or null if it has none. A fresh module on
    // every call.
    public virtual Irie.MachineCode.MachineCodeModule? GetLibrary() => null;

    // Encodes machine code into raw bytes loaded at `origin`.
    public abstract byte[] Encode(Irie.MachineCode.MachineCodeModule module, int origin);

    // As Encode, also returning the absolute address each function and global was laid out at.
    public abstract EncodedProgram EncodeWithSymbols(Irie.MachineCode.MachineCodeModule module, int origin);

    // The whole program as machine code: the post-pipeline module emitted, plus
    // the library routines it references and does not define. Use this rather
    // than MachineCodeEmitter.Emit whenever the result is to be run (listing the
    // functions of the module alone is what Emit is for).
    public Irie.MachineCode.MachineCodeModule EmitProgram(MirModule module)
    {
        var program = MachineCodeEmitter.Emit(module);
        if (GetLibrary() is { } library)
            Irie.MachineCode.MachineCodeLinker.Link(program, library);
        return program;
    }

    // The raw bytes of the whole program, loaded at `origin` (see PackageImage
    // for the target's image format).
    public byte[] BuildProgram(MirModule module, int origin) => Encode(EmitProgram(module), origin);

    // As BuildProgram, also returning the address of each function and global
    // (for a tool that measures a function or reads a global back).
    public EncodedProgram BuildProgramWithSymbols(MirModule module, int origin) =>
        EncodeWithSymbols(EmitProgram(module), origin);

    // Address of the first imaginary zero-page register (RC0). 0 on a bare chip;
    // a system whose low zero-page bytes are hardware (the C64's 6510 port at
    // $00/$01) moves the register file up.
    public virtual int ZeroPageBase => 0;

    // Default origin (load address) when --origin is not supplied; null = no opinion.
    public virtual int? DefaultOrigin => null;

    // Packages a flat 6502 byte buffer into a target-system image format; default is identity.
    public virtual byte[] PackageImage(byte[] code, int origin) => code;
}

// Raw program bytes plus where each function and global ended up.
public sealed record EncodedProgram(
    byte[] Bytes,
    IReadOnlyDictionary<string, int> FunctionAddresses,
    IReadOnlyDictionary<string, int> GlobalAddresses);
