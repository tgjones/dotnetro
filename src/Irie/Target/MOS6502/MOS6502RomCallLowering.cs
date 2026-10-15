namespace Irie.Target.MOS6502;

// The calling convention of a system's ROM entry points (BBC Micro OS, C64
// KERNAL): byte arguments in A, X, Y and byte results back in the same registers.
// What a ROM call may destroy is the registers and flags only: the ROM keeps off
// the user's zero page, so unlike CC_MOS no RC register is clobbered. (OSWORD and
// friends do trash A/X/Y, hence all three are listed whatever the routine.)
internal static class MOS6502RomCallLowering
{
    public static readonly Irie.Target.CallLowering Instance = new MOS6502CallLowering(
        argRegs: [MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y],
        callerSaved:
        [
            MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y,
            MOS6502Registers.C, MOS6502Registers.N, MOS6502Registers.Z,
            MOS6502Registers.V, MOS6502Registers.B,
        ]);
}
