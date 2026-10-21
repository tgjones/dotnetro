namespace System;

/// <summary>
/// A handle to a field with initial data, as `ldtoken` produces for a C# array initializer: the
/// address and size of the data. The compiler builds it, so the layout is the translator's to know.
/// </summary>
public struct RuntimeFieldHandle
{
    internal nint Address;
    internal nint Size;
}
