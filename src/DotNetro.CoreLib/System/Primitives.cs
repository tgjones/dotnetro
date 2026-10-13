namespace System;

// The primitive value types. The compiler maps the ELEMENT_TYPE codes (and so
// their IR types and sizes) itself; these structs exist so that references from
// user programs resolve to a real type with the right size and name. Each has
// the single value field the real corelib has, which gives it its layout:
// int = 32-bit, long = 64-bit, char = 16-bit, nint/nuint = pointer-sized (16-bit).

public readonly struct Boolean { private readonly bool m_value; }

public readonly struct Byte { private readonly byte m_value; }

public readonly struct SByte { private readonly sbyte m_value; }

public readonly struct Int16 { private readonly short m_value; }

public readonly struct UInt16 { private readonly ushort m_value; }

public readonly struct Char { private readonly char m_value; }

public readonly struct Int32 { private readonly int m_value; }

public readonly struct UInt32 { private readonly uint m_value; }

public readonly struct Int64 { private readonly long m_value; }

public readonly struct UInt64 { private readonly ulong m_value; }

public readonly struct IntPtr { private readonly nint m_value; }

public readonly struct UIntPtr { private readonly nuint m_value; }
