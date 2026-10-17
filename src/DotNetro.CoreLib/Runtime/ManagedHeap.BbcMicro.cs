namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    // Below the MODE 7 screen at $7C00.
    private static unsafe partial byte* HeapEnd() => (byte*)0x7C00;
}
