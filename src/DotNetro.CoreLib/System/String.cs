using System.Runtime.CompilerServices;

using DotNetro.Runtime;

namespace System;

/// <summary>
/// A string is a heap object laid out like an array: a null vtable slot, a u16 length
/// (in characters), then one byte per character and a NUL. A reference points at the
/// length, so the two fields below are exactly that header; the characters start at
/// _firstChar and run on past the end of the declared object.
/// </summary>
/// <remarks>
/// Storage is one byte per character (Latin-1), although `char` is 16-bit: a character
/// above 0xFF cannot be stored, and `ldstr` replaces it with '?'.
/// </remarks>
public sealed class String
{
    private ushort _length;
    private byte _firstChar;

    public int Length => _length;

    [IndexerName("Chars")]
    public unsafe char this[int index] => (char)GetChars()[index];

    public static bool IsNullOrEmpty(string value) => (object)value == null || value._length == 0;

    public static string Concat(string str0, string str1)
        => Concat(str0, str1, null, null);

    public static string Concat(string str0, string str1, string str2)
        => Concat(str0, str1, str2, null);

    public static unsafe string Concat(string str0, string str1, string str2, string str3)
    {
        var result = AllocateString(LengthOf(str0) + LengthOf(str1) + LengthOf(str2) + LengthOf(str3));
        var destination = result.GetChars();
        destination = Append(destination, str0);
        destination = Append(destination, str1);
        destination = Append(destination, str2);
        Append(destination, str3);
        return result;
    }

    public static unsafe bool Equals(string a, string b)
    {
        if ((object)a == (object)b)
            return true;
        if ((object)a == null || (object)b == null || a._length != b._length)
            return false;

        var p = a.GetChars();
        var q = b.GetChars();
        for (var remaining = (nint)a._length; remaining > 0; remaining--)
        {
            if (*p != *q)
                return false;
            p++;
            q++;
        }
        return true;
    }

    public bool Equals(string value) => Equals(this, value);

    public override bool Equals(object obj) => obj is string other && Equals(this, other);

    /// <summary>
    /// A string cannot be dispatched through a vtable (it has none), so this and
    /// ToString are reached by direct calls on a string only.
    /// </summary>
    public override unsafe int GetHashCode()
    {
        var hash = 0;
        var p = GetChars();
        for (var remaining = (nint)_length; remaining > 0; remaining--)
        {
            hash = hash * 31 + *p;
            p++;
        }
        return hash;
    }

    public override string ToString() => this;

    public static bool operator ==(string a, string b) => Equals(a, b);

    public static bool operator !=(string a, string b) => !Equals(a, b);

    /// <summary>A new string of `length` characters copied from `source`.</summary>
    internal static unsafe string FromBytes(byte* source, nint length)
    {
        var result = AllocateString(length);
        var destination = result.GetChars();
        var from = source;
        for (var remaining = length; remaining > 0; remaining--)
        {
            *destination = *from;
            destination++;
            from++;
        }
        return result;
    }

    /// <summary>Address of the first character.</summary>
    internal unsafe byte* GetChars()
    {
        fixed (byte* p = &_firstChar)
            return p;
    }

    private static nint LengthOf(string value) => (object)value == null ? 0 : value._length;

    /// <summary>Copies the characters of `value` (null is empty) to `destination`; returns the address after them.</summary>
    private static unsafe byte* Append(byte* start, string value)
    {
        var destination = start;
        if ((object)value != null)
        {
            var source = value.GetChars();
            for (var remaining = (nint)value._length; remaining > 0; remaining--)
            {
                *destination = *source;
                destination++;
                source++;
            }
        }
        return destination;
    }

    /// <summary>
    /// A zeroed string of `length` characters (the NUL terminator comes from the
    /// zeroing). Implemented by the compiler, which allocates the string object.
    /// </summary>
    [MethodImpl(MethodImplOptions.InternalCall)]
    internal static extern string AllocateString(nint length);
}
