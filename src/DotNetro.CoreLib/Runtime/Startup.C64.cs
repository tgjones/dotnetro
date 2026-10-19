using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class Startup
{
    /// <summary>
    /// The soft stack (stack arguments of calls) grows down from the BASIC ROM at
    /// $A000. Its pointer is the compiler's RC0/RC1, which sit at $02/$03 here
    /// ($00/$01 are the 6510 port).
    /// </summary>
    private const ushort SoftStackTop = 0xA000;

    /// <summary>
    /// CHROUT $0E selects the lower/upper-case character set (the PETSCII mapping in
    /// Console.C64.cs assumes it); $93 clears the screen.
    /// </summary>
    public static unsafe partial void Initialize()
    {
        *(ushort*)0x02 = SoftStackTop;

        var chrout = (delegate* unmanaged[C64Kernal]<byte, void>)(nint)0xFFD2;
        chrout(0x0E);
        chrout(0x93);
    }
}
