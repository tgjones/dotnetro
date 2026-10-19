using System.Runtime.CompilerServices;

namespace System;

/// <summary>
/// The public surface is identical for every target and has the same names and
/// signatures as the reference assembly. What differs per target is how bytes reach
/// the machine; that is the partial methods at the bottom, implemented in
/// Console.&lt;Target&gt;.cs (a target that forgets one fails to compile).
/// </summary>
public static partial class Console
{
    /// <summary>PutChar expands CR to CR LF on the BBC Micro, so CR is the newline.</summary>
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

    public static void WriteLine(int value)
    {
        var magnitude = (uint)value;
        if (value < 0)
        {
            PutChar((byte)'-');
            magnitude = 0u - magnitude;
        }
        WriteDigits(magnitude);
        PutChar(13);
    }

    /// <summary>
    /// The decimal digits of `value`, most significant first: the quotient's digits
    /// come first, so recurse on it (at most ten deep) rather than buffer them. The
    /// division by ten is Hacker's Delight's divu10, shifts and adds only: a general
    /// 32-bit software division takes tens of thousands of cycles per number, and
    /// links in its own routine.
    /// </summary>
    private static void WriteDigits(uint value)
    {
        var quotient = (value >> 1) + (value >> 2);
        quotient += quotient >> 4;
        quotient += quotient >> 8;
        quotient += quotient >> 16;
        quotient >>= 3;
        var remainder = value - ((quotient << 3) + (quotient << 1));
        if (remainder > 9)
        {
            quotient++;
            remainder -= 10;
        }

        if (quotient != 0)
            WriteDigits(quotient);
        PutChar((byte)('0' + remainder));
    }

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

    /// <summary>
    /// Platform half. Each name must be unique across the runtime: an InternalCall
    /// method is routed by name to the function of that name in the target's runtime.irie.
    /// </summary>
    private static unsafe partial void PutChar(byte c);

    private static unsafe partial byte* PlatformReadLine();

    private static unsafe partial void PlatformBeep();
}
