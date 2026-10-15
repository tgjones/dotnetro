using System.Runtime.CompilerServices;

namespace System;

// Commodore 64 half of Console. Text goes through the KERNAL: CHROUT ($FFD2) writes
// the character in A to the screen and CHRIN ($FFCF) reads the keyboard through the
// screen editor, both called through function pointers with the `C64Kernal` calling
// convention (byte arguments in A, X, Y). The KERNAL speaks PETSCII, in the
// lower/upper-case character set Startup selects: letters are $41-$5A (lower case)
// and $C1-$DA (upper case), the reverse of ASCII's case order, so the conversion is
// done here. CR ($0D) is the newline in both.
public static partial class Console
{
    private static unsafe partial void PutChar(byte c)
    {
        var petscii = c;
        if (c >= (byte)'a' && c <= (byte)'z')
            petscii = (byte)(c - 0x20);
        else if (c >= (byte)'A' && c <= (byte)'Z')
            petscii = (byte)(c + 0x80);

        ((delegate* unmanaged[C64Kernal]<byte, void>)(nint)0xFFD2)(petscii);
    }

    // CHRIN until the line's CR into the tape buffer ($033C-$03FB, unused here), as ASCII, ending
    // with CR; returns a pointer to it. A line longer than 80 characters (the screen editor's limit
    // is 79) is truncated.
    private static unsafe partial byte* PlatformReadLine()
    {
        var buffer = (byte*)0x033C;
        nint length = 0;
        while (true)
        {
            var c = ((delegate* unmanaged[C64Kernal]<byte>)(nint)0xFFCF)();
            if (c == 13)
                break;

            if (c >= 0x41 && c <= 0x5A)
                c += 0x20;
            else if (c >= 0xC1 && c <= 0xDA)
                c -= 0x80;

            if (length < 80)
            {
                buffer[length] = c;
                length++;
            }
        }
        buffer[length] = 13;
        return buffer;
    }

    // SID voice 1: a triangle wave at full volume for about a fifth of a second. The SID is
    // written directly; there is no ROM routine.
    private static unsafe partial void PlatformBeep()
    {
        var sid = (byte*)0xD400;
        sid[24] = 15;    // volume
        sid[5] = 0x00;   // attack/decay
        sid[6] = 0xF0;   // sustain/release
        sid[1] = 0x1C;   // frequency high (~1 kHz)
        sid[0] = 0xD6;   // frequency low
        sid[4] = 0x11;   // triangle, gate on

        for (ushort i = 0; i < 10000; i++)
        {
        }

        sid[4] = 0x10;   // gate off
    }
}
