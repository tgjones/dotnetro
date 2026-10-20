namespace System.Runtime.CompilerServices;

/// <summary>
/// The handler Roslyn lowers a non-constant interpolated string to. Only the members Roslyn
/// picks for holes of type int, string, bool, char, short and byte exist; alignment and
/// format-string holes are not supported.
/// </summary>
/// <remarks>
/// A plain struct rather than the real ref struct. The destination is a string used as raw
/// storage: it is allocated once at the size hint and trimmed to the characters written by
/// <see cref="ToStringAndClear"/>.
/// </remarks>
public struct DefaultInterpolatedStringHandler
{
    /// <summary>The longest text of an int (-2147483648); the other supported value types are shorter.</summary>
    private const int MaxValueTypeLength = 11;

    private string _buffer;
    private int _count;
    private int _capacity;

    public DefaultInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _capacity = literalLength + MaxValueTypeLength * formattedCount;
        _buffer = String.AllocateString(_capacity);
        _count = 0;
    }

    public void AppendLiteral(string value) => AppendFormatted(value);

    public unsafe void AppendFormatted(string value)
    {
        if ((object)_buffer == null || (object)value == null)
            return;

        var length = value.Length;
        Grow(length);
        String.Append(_buffer.GetChars() + _count, value);
        _count += length;
    }

    /// <remarks>
    /// There is no null check: `value is null` on an unconstrained T boxes a value type just to
    /// test it, which allocates on every call. Every supported T (int, bool, char, short, byte)
    /// is a value type.
    /// </remarks>
    public void AppendFormatted<T>(T value) => AppendFormatted(value.ToString());

    public string ToStringAndClear()
    {
        var result = _buffer;
        _buffer = null;
        if ((object)result == null)
            return "";

        result.Trim(_count);
        return result;
    }

    /// <summary>Makes room for `extra` more characters, replacing the buffer with a larger copy if needed.</summary>
    private unsafe void Grow(int extra)
    {
        var needed = _count + extra;
        if (needed <= _capacity)
            return;

        var capacity = _capacity * 2;
        if (capacity < needed)
            capacity = needed;

        var larger = String.AllocateString(capacity);
        var source = _buffer.GetChars();
        var destination = larger.GetChars();
        for (var i = 0; i < _count; i++)
            destination[i] = source[i];
        _buffer = larger;
        _capacity = capacity;
    }
}
