namespace Irie.Tools.Exec;

/// <summary>
/// Turns the argument domains of an --exec argument into the argument vectors to
/// run. Values are bit patterns masked to each parameter's width.
/// </summary>
internal static class VectorGenerator
{
    /// <summary>The full cross product when it is this small, otherwise a seeded sample.</summary>
    public const int MaxVectors = 256;

    public static List<ulong[]> Generate(IReadOnlyList<ArgumentDomain> domains, IReadOnlyList<int> widths)
    {
        var values = new List<ulong[]>();
        for (var i = 0; i < domains.Count; i++)
            values.Add(Expand(domains[i], widths[i]));

        var total = values.Aggregate(1L, (product, v) => Math.Min(product * v.Length, long.MaxValue / 1024));
        var vectors = new List<ulong[]>();

        if (total <= MaxVectors)
        {
            // Odometer over the cross product, first argument varying slowest.
            var index = new int[values.Count];
            for (var n = 0L; n < total; n++)
            {
                vectors.Add(index.Select((j, i) => values[i][j]).ToArray());
                for (var i = index.Length - 1; i >= 0; i--)
                {
                    if (++index[i] < values[i].Length)
                        break;
                    index[i] = 0;
                }
            }
        }
        else
        {
            var random = new Random(0x1E5);
            var seen = new HashSet<string>();
            while (vectors.Count < MaxVectors)
            {
                var vector = values.Select(v => v[random.Next(v.Length)]).ToArray();
                if (seen.Add(string.Join(",", vector)))
                    vectors.Add(vector);
            }
        }

        return vectors;
    }

    private static ulong[] Expand(ArgumentDomain domain, int bits)
    {
        var mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        IEnumerable<ulong> raw = domain switch
        {
            ArgumentDomain.EdgeValues => EdgeValues(bits),
            ArgumentDomain.Range range => RangeValues(range),
            ArgumentDomain.Listed listed => listed.Values.Select(v => (ulong)v),
            // The vector holds the offset into the buffer; the base address is added later.
            ArgumentDomain.Buffer buffer => buffer.Offsets.Select(v => (ulong)v),
            _ => throw new ArgumentException($"Unknown domain {domain}."),
        };
        return raw.Select(v => v & mask).Distinct().ToArray();
    }

    private static IEnumerable<ulong> RangeValues(ArgumentDomain.Range range)
    {
        if (range.High < range.Low || range.High - range.Low >= 1 << 16)
            throw new ArgumentException($"Bad range {range.Low}..{range.High}: need low <= high and at most 65536 values.");
        for (var v = range.Low; v <= range.High; v++)
            yield return (ulong)v;
    }

    /// <summary>
    /// Zero and one, the neighbourhood of every byte boundary, the signed
    /// extremes, alternating bit patterns, and a few seeded random values.
    /// </summary>
    private static IEnumerable<ulong> EdgeValues(int bits)
    {
        var mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var signBit = 1UL << (bits - 1);

        yield return 0;
        yield return 1;
        yield return 2;
        yield return mask;
        yield return mask - 1;
        yield return signBit;
        yield return signBit + 1;
        yield return signBit - 1;
        yield return 0x5555555555555555UL;
        yield return 0xAAAAAAAAAAAAAAAAUL;

        for (var boundary = 8; boundary < bits; boundary += 8)
        {
            var b = 1UL << boundary;
            yield return b - 1;
            yield return b;
            yield return b + 1;
        }

        var random = new Random(bits);
        for (var i = 0; i < 3; i++)
            yield return random.NextUInt64();
    }

    private static ulong NextUInt64(this Random random) => (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
}
