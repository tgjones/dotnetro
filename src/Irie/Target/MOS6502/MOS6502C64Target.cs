namespace Irie.Target.MOS6502;

// Commodore 64 subtarget: the 6510 is an NMOS 6502 plus an I/O port at $00/$01,
// so codegen is the base chip's with these differences:
//  - the imaginary zero-page registers start at $02, because RC0/RC1 are the soft
//    stack pointer and $00/$01 are the port that banks ROM/RAM/IO;
//  - static frame slots go in $70–$8F, free because the program never calls BASIC
//    (the KERNAL uses $90–$FF and must not be touched);
//  - --emit=bin output is a .prg with a `SYS` stub, loaded at $0801 and entered at
//    $080D.
public sealed class MOS6502C64Target : MOS6502Target
{
    public override int? DefaultOrigin => 0x080D;

    public override int ZeroPageBase => 2;

    protected override string? SystemRuntimeResource => "runtime.c64.irie";

    public override Irie.Target.CallLowering GetCallLowering(int convention) =>
        convention == Irie.Dialects.Call.CallConvention.C64Kernal
            ? MOS6502RomCallLowering.Instance
            : base.GetCallLowering(convention);

    public override void AddPostRegisterAllocationPasses(Irie.Passes.PassManager pm)
    {
        base.AddPostRegisterAllocationPasses(pm);
        pm.AddPass(new MOS6502FramePlacementPass(windowStart: 0x70, windowSize: 0x20));
    }

    public override byte[] PackageImage(byte[] code, int origin) => C64PrgImage.Build(code, origin);
}
