namespace Irie.Target.MOS6502;

// BBC Micro subtarget: identical to MOS6502Target in every codegen aspect,
// but defaults --origin to $2000, promotes non-reentrant frames into the BBC
// Micro's free user zero page, and packages --emit=bin output as a DFS .ssd
// disk image instead of returning raw bytes.
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000;

    // The BBC Micro OS ROM entry points (OSWRCH, OSASCI, OSWORD, …): byte
    // arguments in A, X, Y and byte results back in the same registers. What a
    // ROM call may destroy is the registers and flags only: the OS keeps off the
    // user's zero page, so unlike CC_MOS no RC register is clobbered. (OSWORD and
    // friends do trash A/X/Y, hence all three are listed whatever the routine.)
    private static readonly Irie.Target.CallLowering BbcMicroOSLowering = new MOS6502CallLowering(
        argRegs: [MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y],
        callerSaved:
        [
            MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y,
            MOS6502Registers.C, MOS6502Registers.N, MOS6502Registers.Z,
            MOS6502Registers.V, MOS6502Registers.B,
        ]);

    public override Irie.Target.CallLowering GetCallLowering(int convention) =>
        convention == Irie.Dialects.Call.CallConvention.BbcMicroOS
            ? BbcMicroOSLowering
            : base.GetCallLowering(convention);

    public override void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm)
    {
        base.AddPostRegisterAllocationPasses(pm);

        // The zero-page frame-placement pass is BBC-Micro-specific: $70–$8F is
        // the portable user zero page, which a bare 6502 has no equivalent of, so
        // the base chip never promotes (its slots stay absolute). It runs post-RA
        // and before the generic FrameAccessLoweringPass.
        pm.AddPass(new MOS6502FramePlacementPass());
    }

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(code, loadAddress: origin, execAddress: origin, fileName: "!BOOT");
}
