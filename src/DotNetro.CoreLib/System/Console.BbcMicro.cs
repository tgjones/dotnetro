using System.Runtime.CompilerServices;

namespace System;

// BBC Micro half of Console. The OS ROM entry points are called through function
// pointers at their fixed addresses, with the `BbcMicroOS` calling convention
// (byte arguments in A, X, Y).
public static partial class Console
{
    // OSASCI ($FFE3): write one character, expanding CR to CR LF.
    private static unsafe partial void PutChar(byte c) =>
        ((delegate* unmanaged[BbcMicroOS]<byte, void>)(nint)0xFFE3)(c);

    // OSWORD 0 (read line) into the buffer at $0600; returns a pointer to it. The
    // control block is the buffer address ($0600), the maximum line length ($EE) and
    // the lowest and highest character accepted ($20, $FF), packed little-endian.
    private static unsafe partial byte* PlatformReadLine()
    {
        ulong block = 0x00FF_20EE_0600;
        var address = (nint)(&block);
        ((delegate* unmanaged[BbcMicroOS]<byte, byte, byte, void>)(nint)0xFFF1)(0, (byte)address, (byte)(address >> 8));
        return (byte*)0x0600;
    }

    // OSWORD 7 (SOUND), with X/Y pointing at the control block: four little-endian
    // 16-bit fields, channel 1, amplitude -15, pitch 0, duration 4.
    private static unsafe partial void PlatformBeep()
    {
        ulong block = 0x0004_0000_FFF1_0001;
        var address = (nint)(&block);
        ((delegate* unmanaged[BbcMicroOS]<byte, byte, byte, void>)(nint)0xFFF1)(7, (byte)address, (byte)(address >> 8));
    }
}
