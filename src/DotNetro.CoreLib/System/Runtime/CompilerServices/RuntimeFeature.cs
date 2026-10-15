namespace System.Runtime.CompilerServices;

// Roslyn looks for these fields to learn what the target runtime supports.
public static class RuntimeFeature
{
    // `delegate* unmanaged[Conv]<…>` with an extensible calling convention.
    public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
}
