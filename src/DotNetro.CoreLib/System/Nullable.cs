namespace System;

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
}
