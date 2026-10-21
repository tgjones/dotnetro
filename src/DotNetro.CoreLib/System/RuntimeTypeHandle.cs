namespace System;

/// <summary>
/// A handle to a type: the address of its vtable, which is how a type is identified at run time.
/// The compiler builds it (ldtoken, refanytype), so the layout is the translator's to know.
/// </summary>
public struct RuntimeTypeHandle
{
    internal nint Value;
}
