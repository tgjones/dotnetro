namespace System;

/// <summary>
/// A managed pointer together with the type it points to, as C# `__makeref` produces and `__refvalue`
/// consumes. The compiler builds it (mkrefany), so the layout is the translator's to know. Only the
/// pointer is kept; the type is not recorded yet.
/// </summary>
public struct TypedReference
{
    internal nint Value;
    internal nint Type;
}
