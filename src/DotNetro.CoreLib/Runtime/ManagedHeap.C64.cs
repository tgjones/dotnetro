namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    // The heap runs up to the BASIC ROM at $A000, which stays mapped.
    private static unsafe partial byte* HeapEnd() => (byte*)0xA000;
}
