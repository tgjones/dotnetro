namespace System;

public class Object
{
    public Object()
    {
    }

    /// <summary>Reference equality.</summary>
    public virtual bool Equals(object obj) => (object)this == obj;

    /// <summary>
    /// There is no object identity to hash yet, so every object hashes alike (which is
    /// consistent with Equals, if not well distributed).
    /// </summary>
    public virtual int GetHashCode() => 0;

    /// <summary>There are no run-time types yet, so every object is named for the root type.</summary>
    public virtual string ToString() => "System.Object";

    public static bool Equals(object objA, object objB)
        => (object)objA == objB || ((object)objA != null && (object)objB != null && objA.Equals(objB));

    public static bool ReferenceEquals(object objA, object objB) => (object)objA == objB;
}
