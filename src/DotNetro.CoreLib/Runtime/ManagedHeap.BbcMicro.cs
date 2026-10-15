namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    // Page $10 up: clear of the program (loaded at $2000) and the OS workspace below it.
    private static unsafe partial byte* HeapStart() => (byte*)0x1000;
}
