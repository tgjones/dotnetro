using System;
using System.Text;

namespace Irie.Target.MOS6502;

/// <summary>
/// Packages a flat 6502 byte buffer into a Commodore 64 .prg: a two-byte load
/// address ($0801, the start of BASIC memory) followed by a one-line tokenised
/// BASIC program, `10 SYS &lt;origin&gt;`, so that RUN starts the machine code, then
/// the code itself at &lt;origin&gt;.
/// </summary>
internal static class C64PrgImage
{
    public const int BasicStart = 0x0801;
    private const byte SysToken = 0x9E;
    private const byte BasicLineNumber = 10;

    public static byte[] Build(byte[] code, int origin)
    {
        ArgumentNullException.ThrowIfNull(code);

        var sysArgument = Encoding.ASCII.GetBytes(origin.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // link(2) line number(2) SYS digits... 0 | end-of-program link (2 zero bytes)
        var lineLength = 2 + 2 + 1 + sysArgument.Length + 1;
        var stubEnd = BasicStart + lineLength + 2;
        if (origin < stubEnd)
            throw new ArgumentOutOfRangeException(nameof(origin), $"Origin ${origin:X4} overlaps the BASIC stub, which ends at ${stubEnd:X4}.");
        if (origin + code.Length > 0x10000)
            throw new ArgumentException($"Program of {code.Length} bytes does not fit at ${origin:X4}.", nameof(code));

        var image = new byte[2 + (origin - BasicStart) + code.Length];
        var i = 0;
        image[i++] = BasicStart & 0xFF;
        image[i++] = BasicStart >> 8;

        var nextLine = BasicStart + lineLength;
        image[i++] = (byte)(nextLine & 0xFF);
        image[i++] = (byte)(nextLine >> 8);
        image[i++] = BasicLineNumber;
        image[i++] = 0;
        image[i++] = SysToken;
        foreach (var digit in sysArgument)
            image[i++] = digit;
        image[i++] = 0; // end of line; then the 2-byte end-of-program link, and any padding up to origin

        code.CopyTo(image, 2 + (origin - BasicStart));
        return image;
    }
}
