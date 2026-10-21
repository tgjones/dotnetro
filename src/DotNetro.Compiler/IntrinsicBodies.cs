using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler;

/// <summary>
/// The IL the compiler supplies for the core library's intrinsic methods. Such a method is declared
/// `extern` in C# (the language cannot express its body, e.g. a `cpblk` instruction) and has no
/// body in the assembly; the translator then compiles the IL here, so a call to the method goes
/// through the same code path as the instructions it stands for.
/// </summary>
/// <remarks>
/// Only the core library's methods qualify, so a user method with the same name is unaffected.
/// </remarks>
internal static class IntrinsicBodies
{
    private static readonly Dictionary<(string Type, string Method), byte[]> Bodies = new()
    {
        // Unsafe.CopyBlock(void* destination, void* source, uint byteCount) { cpblk }
        [("System.Runtime.CompilerServices.Unsafe", "CopyBlock")] =
            [0x02, 0x03, 0x04,   // ldarg.0, ldarg.1, ldarg.2
             0xFE, 0x17,         // cpblk
             0x2A],              // ret
        // Unsafe.AsPointer<T>(ref T value) { return value's address; }: a managed pointer is already one.
        [("System.Runtime.CompilerServices.Unsafe", "AsPointer")] =
            [0x02,               // ldarg.0
             0x2A],              // ret

        // MemoryMarshal.GetArrayDataReference(Array array): a reference to the first element, which
        // follows the array's 2-byte length (see ManagedHeap.AllocArray).
        [("System.Runtime.InteropServices.MemoryMarshal", "GetArrayDataReference")] =
            [0x02,               // ldarg.0
             0x18,               // ldc.i4.2
             0x58,               // add
             0x2A],              // ret

        // Unsafe.InitBlock(void* startAddress, byte value, uint byteCount) { initblk }
        [("System.Runtime.CompilerServices.Unsafe", "InitBlock")] =
            [0x02, 0x03, 0x04,   // ldarg.0, ldarg.1, ldarg.2
             0xFE, 0x18,         // initblk
             0x2A],              // ret
    };

    /// <summary>The IL of the intrinsic `method` of `type`, or null if it is not one.</summary>
    public static byte[]? TryGet(EcmaType type, string method) =>
        type.Assembly == type.Context.CoreAssembly && Bodies.TryGetValue((type.FullName, method), out var il) ? il : null;
}
