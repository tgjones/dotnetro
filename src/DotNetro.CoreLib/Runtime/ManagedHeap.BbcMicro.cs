namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    /// <summary>Below the MODE 7 screen at $7C00.</summary>
    private static unsafe partial byte* HeapEnd() => (byte*)0x7C00;
}
