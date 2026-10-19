using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class Startup
{
    /// <summary>
    /// The soft stack (stack arguments of calls) grows down from the MODE 7 screen
    /// at &amp;7C00. Its pointer is the compiler's RC0/RC1, the first two zero-page bytes.
    /// </summary>
    private const ushort SoftStackTop = 0x7C00;

    /// <summary>VDU 22, 7 selects MODE 7.</summary>
    public static unsafe partial void Initialize()
    {
        *(ushort*)0x00 = SoftStackTop;

        var oswrch = (delegate* unmanaged[BbcMicroOS]<byte, void>)(nint)0xFFEE;
        oswrch(22);
        oswrch(7);
    }
}
