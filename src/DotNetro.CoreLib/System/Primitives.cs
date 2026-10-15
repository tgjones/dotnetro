namespace System;

// The primitive value types. The compiler maps the ELEMENT_TYPE codes (and so
// their IR types and sizes) itself; these structs exist so that references from
// user programs resolve to a real type with the right size and name. Each has
// the single value field the real corelib has, which gives it its layout:
// int = 32-bit, long = 64-bit, char = 16-bit, nint/nuint = pointer-sized (16-bit).

public readonly struct Boolean
{
    private readonly bool m_value;

    public override string ToString() => m_value ? "True" : "False";

    public override bool Equals(object obj) => obj is bool other && other == m_value;

    public override int GetHashCode() => m_value ? 1 : 0;
}

public readonly struct Byte { private readonly byte m_value; }

public readonly struct SByte { private readonly sbyte m_value; }

public readonly struct Int16 { private readonly short m_value; }

public readonly struct UInt16 { private readonly ushort m_value; }

public readonly struct Char { private readonly char m_value; }

public readonly struct Int32
{
    private readonly int m_value;

    public override unsafe string ToString()
    {
        var negative = m_value < 0;
        var magnitude = negative ? (uint)-m_value : (uint)m_value;

        var digits = 1;
        for (var rest = magnitude; rest >= 10; rest /= 10)
            digits++;

        var length = negative ? digits + 1 : digits;
        var result = String.AllocateString(length);
        var chars = result.GetChars();
        if (negative)
            *chars = (byte)'-';

        var p = chars + length;
        do
        {
            p--;
            *p = (byte)('0' + magnitude % 10);
            magnitude /= 10;
        }
        while (magnitude != 0);

        return result;
    }

    public override bool Equals(object obj) => obj is int other && other == m_value;

    public override int GetHashCode() => m_value;
}

public readonly struct UInt32 { private readonly uint m_value; }

public readonly struct Int64 { private readonly long m_value; }

public readonly struct UInt64 { private readonly ulong m_value; }

public readonly struct IntPtr { private readonly nint m_value; }

public readonly struct UIntPtr { private readonly nuint m_value; }
