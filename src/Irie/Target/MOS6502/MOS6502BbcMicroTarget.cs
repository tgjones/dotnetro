namespace Irie.Target.MOS6502;

/// <summary>
/// BBC Micro subtarget: identical to MOS6502Target in every codegen aspect,
/// but defaults --origin to $2000, promotes non-reentrant frames into the free
/// user zero page, and packages --emit=bin output as a DFS .ssd
/// disk image instead of returning raw bytes.
/// </summary>
public sealed class MOS6502BbcMicroTarget : MOS6502Target
{
    public override int? DefaultOrigin => 0x2000;

    public override Irie.Target.CallLowering GetCallLowering(int convention) =>
        convention == Irie.Dialects.Call.CallConvention.BbcMicroOS
            ? MOS6502RomCallLowering.Instance
            : base.GetCallLowering(convention);

    protected override bool PromoteFrameSlots => true;

    public override byte[] PackageImage(byte[] code, int origin)
        => BbcMicroDfsImage.Build(code, loadAddress: origin, execAddress: origin, fileName: "!BOOT");
}
