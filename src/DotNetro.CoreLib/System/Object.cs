namespace System;

public class Object
{
    public Object()
    {
    }

    // Reference equality.
    public virtual bool Equals(object obj) => (object)this == obj;

    // There is no object identity to hash yet, so every object hashes alike (which is
    // consistent with Equals, if not well distributed).
    public virtual int GetHashCode() => 0;

    // There are no run-time types yet, so every object is named for the root type.
    public virtual string ToString() => "System.Object";

    public static bool Equals(object objA, object objB)
        => (object)objA == objB || ((object)objA != null && (object)objB != null && objA.Equals(objB));

    public static bool ReferenceEquals(object objA, object objB) => (object)objA == objB;
}
