using System.Runtime.CompilerServices;

namespace System;

// BBC Micro half of Console. The bodies are MOS6502 MIR in runtime.irie because they
// JSR a fixed ROM vector with arguments in specific registers, which C# cannot express
// yet (stage 6 of notes/custom-corelib-plan.md).
public static partial class Console
{
    // OSASCI ($FFE3): write one character, expanding CR to CR LF. The i8 argument
    // arrives in $a per CC_MOS, which is exactly what the ROM routine reads.
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern partial void PutChar(byte c);

    // Prints a signed 32-bit integer in decimal followed by CR.
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern partial void PlatformWriteLineInt32(int value);

    // OSWORD 0 into the buffer at $0600; returns a pointer to it.
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern unsafe partial byte* PlatformReadLine();

    // OSWORD 7 (SOUND).
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern partial void PlatformBeep();
}
