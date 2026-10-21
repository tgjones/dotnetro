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
    /// The lowest address handed out by LocalAlloc: the localloc area grows down from the end
    /// of the heap while the heap grows up from its start, and they meet when memory runs out.
    /// </summary>
    private static unsafe byte* s_localPtr = HeapEnd();

    /// <summary>
    /// Allocate `size` bytes of zeroed object contents behind a vtable header.
    /// Returns a pointer to the contents.
    /// </summary>
    public static unsafe byte* Alloc(nint size, void* vtablePtr)
    {
        var result = s_heapPtr;

        // Out of memory: there are no exceptions, so stop.
        if (size > (nint)(s_localPtr - result) - sizeof(IntPtr))
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

    /// <summary>
    /// The current top of the localloc area. A method that uses localloc takes this on entry and
    /// hands it back to LocalAllocRelease when it returns, which frees what it allocated.
    /// </summary>
    public static unsafe byte* LocalAllocMark() => s_localPtr;

    /// <summary>Free everything LocalAlloc handed out since `mark` was taken.</summary>
    public static unsafe void LocalAllocRelease(byte* mark)
    {
        s_localPtr = mark;
    }

    /// <summary>
    /// The localloc IL opcode in a method with initialised locals (the default; C# sets it unless
    /// [SkipLocalsInit] is applied): `size` bytes of zeroed memory that live until the allocating
    /// method returns. Returns a pointer to the first byte.
    /// </summary>
    public static unsafe byte* LocalAlloc(nint size)
    {
        var result = LocalAllocUninitialized(size);

        var contents = result;
        for (var remaining = size; remaining > 0; remaining--)
        {
            *contents = 0;
            contents++;
        }

        return result;
    }

    /// <summary>
    /// The localloc IL opcode in a method without initialised locals: like LocalAlloc, but the
    /// memory keeps whatever it held before.
    /// </summary>
    public static unsafe byte* LocalAllocUninitialized(nint size)
    {
        // Out of memory: there are no exceptions, so stop.
        if (size > (nint)(s_localPtr - s_heapPtr))
        {
            while (true)
            {
            }
        }

        s_localPtr -= size;
        return s_localPtr;
    }
}
