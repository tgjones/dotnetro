using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices;

public static unsafe class RuntimeHelpers
{
    /// <summary>
    /// Copy the data a field handle names into an array: the C# compiler's way of initialising an
    /// array from a constant list (`new int[] { 1, 2, 3 }`).
    /// </summary>
    public static void InitializeArray(Array array, RuntimeFieldHandle fldHandle)
    {
        Unsafe.CopyBlock(Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array)), (void*)fldHandle.Address, (uint)fldHandle.Size);
    }
}
