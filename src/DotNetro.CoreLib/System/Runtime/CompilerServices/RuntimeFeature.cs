namespace System.Runtime.CompilerServices;

/// <summary>Roslyn looks for these fields to learn what the target runtime supports.</summary>
public static class RuntimeFeature
{
    /// <summary>`delegate* unmanaged[Conv]&lt;…&gt;` with an extensible calling convention.</summary>
    public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
}
