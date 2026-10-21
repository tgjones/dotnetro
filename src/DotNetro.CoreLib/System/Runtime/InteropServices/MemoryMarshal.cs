namespace System.Runtime.InteropServices;

public static unsafe class MemoryMarshal
{
    /// <summary>
    /// A reference to the first element of `array`, whatever its element type (the compiler supplies
    /// the body, as only it knows the array layout: a 2-byte length, then the elements).
    /// </summary>
#pragma warning disable CS0626 // extern without attributes: the compiler supplies the body
    public static extern ref byte GetArrayDataReference(Array array);

    /// <summary>A reference to the first element of `array` (the compiler supplies the body).</summary>
    public static extern ref T GetArrayDataReference<T>(T[] array);
#pragma warning restore CS0626
}
