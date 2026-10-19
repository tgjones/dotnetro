using System;
using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static partial class ManagedHeap
{
    /// <summary>
    /// Where the heap starts: right after the program image (the encoder's @__image_end,
    /// via @HeapStart in runtime.irie), the same on every target.
    /// </summary>
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static unsafe extern byte* HeapStart();

    /// <summary>The first address past the heap, per target.</summary>
    private static unsafe partial byte* HeapEnd();

    private static unsafe byte* s_heapPtr = HeapStart();

    /// <summary>
    /// Allocate `size` bytes of zeroed object contents behind a vtable header.
    /// Returns a pointer to the contents.
    /// </summary>
    public static unsafe byte* Alloc(nint size, void* vtablePtr)
    {
        var result = s_heapPtr;

        // Out of memory: there are no exceptions, so stop.
        if (size > (nint)(HeapEnd() - result) - sizeof(IntPtr))
        {
            while (true)
            {
            }
        }

        // Increment by size of object header + size of object.
        s_heapPtr += sizeof(IntPtr) + size;

        // Fill object header with vtable pointer.
        *(void**)result = vtablePtr;

        // Zero the object contents; the heap memory is not known to be clear.
        var contents = result + sizeof(IntPtr);
        for (var remaining = size; remaining > 0; remaining--)
        {
            *contents = 0;
            contents++;
        }

        // Return pointer to object contents.
        return result + sizeof(IntPtr);
    }

    /// <summary>
    /// Allocate an array: a null vtable header, a 2-byte length, then
    /// `contentSize` bytes of (zeroed) elements. Returns a pointer to the length.
    /// </summary>
    public static unsafe byte* AllocArray(nint length, nint contentSize)
    {
        var result = Alloc(sizeof(IntPtr) + contentSize, null);
        *(nint*)result = length;
        return result;
    }
}
