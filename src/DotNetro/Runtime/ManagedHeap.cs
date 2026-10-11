namespace DotNetro.Runtime;

internal static class ManagedHeap
{
    private static IntPtr s_heapPtr = 0x1000;

    // Allocate `size` bytes of zeroed object contents behind a vtable header.
    // Returns a pointer to the contents.
    public static unsafe IntPtr Alloc(nint size, IntPtr vtablePtr)
    {
        var result = s_heapPtr;

        // Increment by size of object header + size of object.
        s_heapPtr += sizeof(IntPtr) + size;

        // Fill object header with vtable pointer.
        *(IntPtr*)result = vtablePtr;

        // Zero the object contents; the heap memory is not known to be clear.
        var contents = (byte*)(result + sizeof(IntPtr));
        for (var remaining = size; remaining > 0; remaining--)
        {
            *contents = 0;
            contents++;
        }

        // Return pointer to object contents.
        return result + sizeof(IntPtr);
    }

    // Allocate an array: a null vtable header, a 2-byte length, then
    // `contentSize` bytes of (zeroed) elements. Returns a pointer to the length.
    public static unsafe IntPtr AllocArray(nint length, nint contentSize)
    {
        var result = Alloc(sizeof(IntPtr) + contentSize, default);
        *(nint*)result = length;
        return result;
    }
}
