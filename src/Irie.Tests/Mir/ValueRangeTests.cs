using Irie.Dialects.Arith;
using Irie.Mir;

namespace Irie.Tests.Mir;

/// <summary>Pins <see cref="ValueRange.TryDecide"/> at the boundaries where a compare stops being decided.</summary>
public sealed class ValueRangeTests
{
    [Test]
    [Arguments(ArithCmpPredicate.Ult, 0, 255, 256, 256, true)]
    [Arguments(ArithCmpPredicate.Ult, 0, 255, 255, 255, null)]
    [Arguments(ArithCmpPredicate.Uge, 0, 255, 256, 256, false)]
    [Arguments(ArithCmpPredicate.Ule, 0, 255, 255, 255, true)]
    [Arguments(ArithCmpPredicate.Ugt, 0, 255, 255, 255, false)]
    [Arguments(ArithCmpPredicate.Ugt, 0, 255, 254, 254, null)]
    [Arguments(ArithCmpPredicate.Slt, -5, -1, 0, 0, true)]
    [Arguments(ArithCmpPredicate.Slt, -5, 0, 0, 0, null)]
    [Arguments(ArithCmpPredicate.Sge, -5, -1, 0, 0, false)]
    [Arguments(ArithCmpPredicate.Sle, -5, 0, 0, 0, true)]
    [Arguments(ArithCmpPredicate.Sgt, 1, 9, 0, 0, true)]
    [Arguments(ArithCmpPredicate.Sgt, 0, 9, 0, 0, null)]
    public async Task OrderedPredicates(ArithCmpPredicate predicate, int lo, int hi, int otherLo, int otherHi, bool? expected)
    {
        await Assert.That(new ValueRange(lo, hi).TryDecide(predicate, new ValueRange(otherLo, otherHi)))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments(ArithCmpPredicate.Ult)]
    [Arguments(ArithCmpPredicate.Ule)]
    [Arguments(ArithCmpPredicate.Ugt)]
    [Arguments(ArithCmpPredicate.Uge)]
    public async Task UnsignedPredicate_OnPossiblyNegativeRange_IsUndecided(ArithCmpPredicate predicate)
    {
        // -1 reads as a large unsigned value, so [-5, -1] u< [0, 0] would be wrongly "false" by interval order.
        await Assert.That(new ValueRange(-5, -1).TryDecide(predicate, new ValueRange(0, 0))).IsNull();
        await Assert.That(new ValueRange(0, 0).TryDecide(predicate, new ValueRange(-5, -1))).IsNull();
    }

    [Test]
    [Arguments(ArithCmpPredicate.Eq, 0, 3, 4, 9, false)]
    [Arguments(ArithCmpPredicate.Ne, 0, 3, 4, 9, true)]
    [Arguments(ArithCmpPredicate.Eq, 7, 7, 7, 7, true)]
    [Arguments(ArithCmpPredicate.Ne, 7, 7, 7, 7, false)]
    [Arguments(ArithCmpPredicate.Eq, 0, 3, 3, 9, null)]
    [Arguments(ArithCmpPredicate.Ne, 0, 3, 0, 3, null)]
    public async Task EqualityPredicates(ArithCmpPredicate predicate, int lo, int hi, int otherLo, int otherHi, bool? expected)
    {
        await Assert.That(new ValueRange(lo, hi).TryDecide(predicate, new ValueRange(otherLo, otherHi)))
            .IsEqualTo(expected);
    }
}
