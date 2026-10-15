using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class Startup
{
    // VDU 22, 7 selects MODE 7.
    public static unsafe partial void Initialize()
    {
        var oswrch = (delegate* unmanaged[BbcMicroOS]<byte, void>)(nint)0xFFEE;
        oswrch(22);
        oswrch(7);
    }
}
