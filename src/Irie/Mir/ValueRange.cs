using Irie.Dialects.Arith;

namespace Irie.Mir;

/// <summary>
/// A closed interval <c>[Lo, Hi]</c> of the values an integer vreg can hold, over the signed reading of
/// the vreg's width (i1 is read unsigned, so its full range is [0, 1]). Bounds are <see cref="Int128"/> so
/// every width up to i64 is representable. The full range for the width means "unknown".
/// </summary>
public sealed record ValueRange(Int128 Lo, Int128 Hi)
{
    /// <summary>The range of every value of the type: the "unknown" range.</summary>
    public static ValueRange Full(int bits) => bits == 1
        ? new ValueRange(0, 1)
        : new ValueRange(-(Int128.One << (bits - 1)), (Int128.One << (bits - 1)) - 1);

    public static ValueRange Constant(Int128 value) => new(value, value);

    /// <summary>The range of an unsigned <paramref name="bits"/>-bit value: [0, 2^bits - 1].</summary>
    public static ValueRange Unsigned(int bits) => new(0, (Int128.One << bits) - 1);

    public static ValueRange Signed(int bits) => new(-(Int128.One << (bits - 1)), (Int128.One << (bits - 1)) - 1);

    /// <summary>True when this is the full range of an integer of <paramref name="bits"/> bits.</summary>
    public bool IsFull(int bits) => this == Full(bits);

    /// <summary>All values lie in [0, 2^bits).</summary>
    public bool FitsUnsigned(int bits) => Lo >= 0 && Hi < (Int128.One << bits);

    /// <summary>All values lie in [-2^(bits-1), 2^(bits-1)).</summary>
    public bool FitsSigned(int bits) =>
        Lo >= -(Int128.One << (bits - 1)) && Hi < (Int128.One << (bits - 1));

    /// <summary>The smallest interval containing both ranges.</summary>
    public ValueRange Join(ValueRange other) => new(Int128.Min(Lo, other.Lo), Int128.Max(Hi, other.Hi));

    /// <summary>The overlap of both ranges, or null when they are disjoint.</summary>
    public ValueRange? Intersect(ValueRange other)
    {
        var lo = Int128.Max(Lo, other.Lo);
        var hi = Int128.Min(Hi, other.Hi);
        return lo <= hi ? new ValueRange(lo, hi) : null;
    }

    public bool Contains(Int128 value) => value >= Lo && value <= Hi;

    /// <summary>
    /// Whether <c>x predicate y</c> holds for every pair with x in this range and y in <paramref name="other"/>
    /// (true), for no pair (false), or for some pairs only (null). Unsigned predicates are decided only when both
    /// ranges are non-negative: a range that may be negative holds large unsigned values, which this interval
    /// reading does not order.
    /// </summary>
    public bool? TryDecide(ArithCmpPredicate predicate, ValueRange other)
    {
        var unsigned = predicate is ArithCmpPredicate.Ult or ArithCmpPredicate.Ugt
            or ArithCmpPredicate.Ule or ArithCmpPredicate.Uge;
        if (unsigned && (Lo < 0 || other.Lo < 0))
            return null;

        var disjoint = Hi < other.Lo || other.Hi < Lo;
        var sameValue = Lo == Hi && other.Lo == other.Hi && Lo == other.Lo;
        return predicate switch
        {
            ArithCmpPredicate.Eq => sameValue ? true : disjoint ? false : null,
            ArithCmpPredicate.Ne => sameValue ? false : disjoint ? true : null,
            ArithCmpPredicate.Slt or ArithCmpPredicate.Ult => Hi < other.Lo ? true : Lo >= other.Hi ? false : null,
            ArithCmpPredicate.Sle or ArithCmpPredicate.Ule => Hi <= other.Lo ? true : Lo > other.Hi ? false : null,
            ArithCmpPredicate.Sgt or ArithCmpPredicate.Ugt => Lo > other.Hi ? true : Hi <= other.Lo ? false : null,
            ArithCmpPredicate.Sge or ArithCmpPredicate.Uge => Lo >= other.Hi ? true : Hi < other.Lo ? false : null,
            _ => null,
        };
    }

    public override string ToString() => $"[{Lo}, {Hi}]";
}
