using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    // Right after the program image (the encoder's @__image_end, via runtime.c64.irie).
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static unsafe extern partial byte* HeapStart();

    // The heap runs up to the BASIC ROM at $A000, which stays mapped.
    private static unsafe partial byte* HeapEnd() => (byte*)0xA000;
}
