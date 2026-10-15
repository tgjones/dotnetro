using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class Startup
{
    // CHROUT $0E selects the lower/upper-case character set (the PETSCII mapping in
    // Console.C64.cs assumes it); $93 clears the screen.
    public static unsafe partial void Initialize()
    {
        var chrout = (delegate* unmanaged[C64Kernal]<byte, void>)(nint)0xFFD2;
        chrout(0x0E);
        chrout(0x93);
    }
}
