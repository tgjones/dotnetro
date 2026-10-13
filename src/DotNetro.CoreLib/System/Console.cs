using System.Runtime.CompilerServices;

namespace System;

// The public surface is identical for every target and has the same names and
// signatures as the reference assembly. What differs per target is how bytes reach
// the machine; that is the partial methods at the bottom, implemented in
// Console.<Target>.cs (a target that forgets one fails to compile).
public static partial class Console
{
    // PutChar expands CR to CR LF on the BBC Micro, so CR is the newline.
    public static unsafe void WriteLine(string value)
    {
        if ((object)value != null)
        {
            var p = value.GetChars();
            for (var remaining = (nint)value.Length; remaining > 0; remaining--)
            {
                PutChar(*p);
                p++;
            }
        }
        PutChar(13);
    }

    public static void WriteLine(char value)
    {
        PutChar((byte)value);
        PutChar(13);
    }

    public static void WriteLine(int value) => PlatformWriteLineInt32(value);

    public static void WriteLine(bool value)
    {
        if (value)
            WriteLine("True");
        else
            WriteLine("False");
    }

    public static unsafe string ReadLine()
    {
        // The platform leaves the line in a buffer, terminated by CR.
        var line = PlatformReadLine();
        nint length = 0;
        while (line[length] != 13)
            length++;
        return String.FromBytes(line, length);
    }

    public static void Beep() => PlatformBeep();

    // Platform half. Each name must be unique across the runtime: an InternalCall
    // method is routed by name to the function of that name in the target's runtime.irie.
    private static partial void PutChar(byte c);

    private static partial void PlatformWriteLineInt32(int value);

    private static unsafe partial byte* PlatformReadLine();

    private static partial void PlatformBeep();
}
