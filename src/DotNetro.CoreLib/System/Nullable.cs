namespace System;

/// <summary>
/// A value of <typeparamref name="T"/> that may be absent. C# lowers <c>int?</c> operators, <c>??</c>
/// and <c>.HasValue</c> to calls on this type, so the members the compiler emits are all here.
/// </summary>
/// <remarks>
/// There are no exceptions yet, so <see cref="Value"/> on an empty instance returns
/// <c>default(T)</c> rather than throwing. Boxing a <c>Nullable&lt;T&gt;</c> boxes the struct itself
/// (the CLR boxes the underlying value, or yields null when empty).
/// </remarks>
public struct Nullable<T> where T : struct
{
    private bool hasValue;
    private T value;

    public Nullable(T value)
    {
        this.value = value;
        hasValue = true;
    }

    public readonly bool HasValue => hasValue;

    public readonly T Value => value;

    public readonly T GetValueOrDefault() => value;

    public readonly T GetValueOrDefault(T defaultValue) => hasValue ? value : defaultValue;
}
