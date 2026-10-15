using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

public sealed class C64PrgImageTests
{
    [Test]
    public async Task HeaderIsLoadAddressThenSysLine()
    {
        var image = C64PrgImage.Build([0xEA, 0x60], origin: 0x080D);

        // load $0801 | link $080B, line 10, SYS "2061", EOL, end of program | code
        await Assert.That(image).IsEquivalentTo(new byte[]
        {
            0x01, 0x08,
            0x0B, 0x08, 0x0A, 0x00, 0x9E, 0x32, 0x30, 0x36, 0x31, 0x00, 0x00, 0x00,
            0xEA, 0x60,
        });
    }

    [Test]
    [Arguments(0x080D, "2061")]
    [Arguments(0x0900, "2304")]
    [Arguments(0xC000, "49152")]
    public async Task SysArgumentIsTheDecimalOrigin(int origin, string digits)
    {
        var image = C64PrgImage.Build([0x60], origin);

        // The line is link(2) number(2) token digits NUL; the code sits at origin - $0801 + 2 in the file.
        await Assert.That(System.Text.Encoding.ASCII.GetString(image, 7, digits.Length)).IsEqualTo(digits);
        await Assert.That(image[7 + digits.Length]).IsEqualTo((byte)0);
        await Assert.That(image.Length).IsEqualTo(2 + (origin - 0x0801) + 1);
        await Assert.That(image[^1]).IsEqualTo((byte)0x60);
        // The link points at the end-of-program marker just after the line.
        var link = image[2] | (image[3] << 8);
        await Assert.That(link).IsEqualTo(0x0801 + 6 + digits.Length);
    }

    [Test]
    public async Task OriginInsideTheStubIsRejected()
    {
        await Assert.That(() => C64PrgImage.Build([0x60], origin: 0x0805)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ProgramRunningPastTheEndOfMemoryIsRejected()
    {
        await Assert.That(() => C64PrgImage.Build(new byte[0x100], origin: 0xFF80)).Throws<ArgumentException>();
    }
}
