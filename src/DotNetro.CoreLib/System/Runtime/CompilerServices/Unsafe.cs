namespace System.Runtime.CompilerServices;

/// <summary>
/// The subset of System.Runtime.CompilerServices.Unsafe the compiler needs. In the real library these
/// are single IL instructions, which C# cannot express: they are declared `extern` and the compiler
/// supplies their bodies (see IntrinsicBodies in DotNetro.Compiler).
/// </summary>
public static unsafe class Unsafe
{
    /// <summary>
    /// Copy `byteCount` bytes from `source` to `destination` (the cpblk instruction). The blocks must
    /// not overlap. The count is truncated to the 16-bit address space.
    /// </summary>
#pragma warning disable CS0626 // extern without attributes: the compiler supplies the body
    public static extern void CopyBlock(void* destination, void* source, uint byteCount);

    /// <summary>The address `value` refers to, as an unmanaged pointer (the compiler supplies the body).</summary>
    public static extern void* AsPointer<T>(ref T value);

    /// <summary>
    /// Set `byteCount` bytes at `startAddress` to `value` (the initblk instruction). The count is
    /// truncated to the 16-bit address space.
    /// </summary>
    public static extern void InitBlock(void* startAddress, byte value, uint byteCount);
#pragma warning restore CS0626
}
