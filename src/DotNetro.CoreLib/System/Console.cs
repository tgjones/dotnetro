using System.Runtime.CompilerServices;

namespace System;

// The public surface is identical for every target and has the same names and
// signatures as the reference assembly. What differs per target is how bytes reach
// the machine; that is the partial methods at the bottom, implemented in
// Console.<Target>.cs (a target that forgets one fails to compile).
public static partial class Console
{
    // A string constant is a pointer to its ASCII bytes followed by CR (0x0D) and NUL
    // (see RegisterStringGlobal in the translator), so the CR is the newline. PutChar
    // expands CR to CR LF on the BBC Micro. Stage 3 gives String a real layout.
    public static unsafe void WriteLine(string value)
    {
        var p = StringBytes(value);
        byte c;
        while ((c = *p) != 0)
        {
            PutChar(c);
            p++;
        }
    }

    public static void WriteLine(int value) => PlatformWriteLineInt32(value);

    public static void WriteLine(bool value)
    {
        if (value)
            WriteLine("True");
        else
            WriteLine("False");
    }

    public static string ReadLine() => PlatformReadLine();

    public static void Beep() => PlatformBeep();

    // The string reference is already a pointer to its bytes; this reinterprets it.
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern unsafe byte* StringBytes(string value);

    // Platform half. Each name must be unique across the runtime: an InternalCall
    // method is routed by name to the function of that name in the target's runtime.irie.
    private static partial void PutChar(byte c);

    private static partial void PlatformWriteLineInt32(int value);

    private static partial string PlatformReadLine();

    private static partial void PlatformBeep();
}
