namespace System;

/// <summary>
/// A run-time type, as `typeof(T)` and `__reftype` produce. It is only its handle: types compare equal
/// when their handles (vtable addresses) do. Each `typeof` allocates a new object, so compare with
/// `==` or Equals, not by reference.
/// </summary>
public sealed class Type
{
    private readonly RuntimeTypeHandle _handle;

    private Type(RuntimeTypeHandle handle)
    {
        _handle = handle;
    }

    public RuntimeTypeHandle TypeHandle => _handle;

    public static Type GetTypeFromHandle(RuntimeTypeHandle handle) => new Type(handle);

    public static bool operator ==(Type left, Type right)
    {
        if ((object)left == null || (object)right == null)
            return (object)left == (object)right;
        return left._handle.Value == right._handle.Value;
    }

    public static bool operator !=(Type left, Type right) => !(left == right);

    public bool Equals(Type other) => this == other;

    public override bool Equals(object obj) => obj is Type other && this == other;

    public override int GetHashCode() => (int)_handle.Value;
}
