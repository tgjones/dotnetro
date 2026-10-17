using System.Globalization;
using System.Text.RegularExpressions;

namespace Irie.Tools.Exec;

// `--exec "name(arg, arg, ...)"` -- one function to run and the values to run it
// with. Each argument is
//   *          the default edge values for its width
//   lo..hi     every integer in the inclusive range
//   a|b|c      exactly these values
//   v          a single value
//   buf(N)     a pointer to a fresh N-byte buffer of seeded bytes (a global, so its
//              contents are compared after the run)
//   buf(N)@a|b a pointer into such a buffer at each of these byte offsets
// Numbers are decimal or 0x-hex and may be negative (two's complement at the
// parameter's width).
internal sealed record ExecDirective(string Function, IReadOnlyList<ArgumentDomain> Arguments)
{
    private static readonly Regex Syntax = new(@"^\s*(?<name>\w+)\s*\((?<args>.*)\)\s*$");

    public static ExecDirective Parse(string text)
    {
        var match = Syntax.Match(text);
        if (!match.Success)
            throw new ArgumentException($"Bad --exec value '{text}': expected name(arg, arg, ...).");

        var args = match.Groups["args"].Value;
        var domains = args.Trim().Length == 0
            ? []
            : args.Split(',').Select(a => ArgumentDomain.Parse(a.Trim())).ToList();
        return new ExecDirective(match.Groups["name"].Value, domains);
    }
}

internal abstract record ArgumentDomain
{
    public static ArgumentDomain Parse(string text)
    {
        if (text == "*")
            return new EdgeValues();

        var buffer = BufferSyntax.Match(text);
        if (buffer.Success)
        {
            var offsets = buffer.Groups["offsets"].Success
                ? buffer.Groups["offsets"].Value.Split('|').Select(ParseNumber).ToArray()
                : [0L];
            return new Buffer(int.Parse(buffer.Groups["size"].Value, CultureInfo.InvariantCulture), offsets);
        }

        if (text.Contains(".."))
        {
            var parts = text.Split("..", 2);
            return new Range(ParseNumber(parts[0]), ParseNumber(parts[1]));
        }

        return new Listed(text.Split('|').Select(ParseNumber).ToArray());
    }

    private static readonly Regex BufferSyntax = new(@"^buf\((?<size>\d+)\)(?:@(?<offsets>.+))?$");

    private static long ParseNumber(string text)
    {
        text = text.Trim();
        var negative = text.StartsWith('-');
        if (negative)
            text = text[1..];

        var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    internal sealed record EdgeValues : ArgumentDomain;
    internal sealed record Range(long Low, long High) : ArgumentDomain;
    internal sealed record Listed(long[] Values) : ArgumentDomain;
    internal sealed record Buffer(int Size, long[] Offsets) : ArgumentDomain;
}
