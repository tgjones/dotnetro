namespace Irie.Target.MOS6502;

/// <summary>
/// Commodore 64 subtarget: the 6510 is an NMOS 6502 plus an I/O port at $00/$01,
/// so codegen is the base chip's with these differences:
///  - the imaginary zero-page registers start at $02, because RC0/RC1 are the soft
///    stack pointer and $00/$01 are the port that banks ROM/RAM/IO;
///  - homes and static frame slots go in $16–$8F, free because the program never
///    calls BASIC (the KERNAL uses $90–$FF and must not be touched);
///  - --emit=bin output is a .prg with a `SYS` stub, loaded at $0801 and entered at
///    $080D.
/// </summary>
public sealed class MOS6502C64Target : MOS6502Target
{
    public override int? DefaultOrigin => 0x080D;

    public override int ZeroPageBase => 2;

    public override Irie.Target.CallLowering GetCallLowering(int convention) =>
        convention == Irie.Dialects.Call.CallConvention.C64Kernal
            ? MOS6502RomCallLowering.Instance
            : base.GetCallLowering(convention);

    protected override bool PromoteFrameSlots => true;

    public override byte[] PackageImage(byte[] code, int origin) => C64PrgImage.Build(code, origin);
}
