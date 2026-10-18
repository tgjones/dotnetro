using Irie.Target;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target;

public sealed class PlacementTuningTests
{
    [Test]
    public async Task Parse_ReadsEveryKnob()
    {
        var tuning = PlacementTuning.Parse("beam=0,size-weight=2,speed-weight=3,lookahead=1,freq-base=4,choose-conventions");

        await Assert.That(tuning.BeamWidth).IsEqualTo(0);
        await Assert.That(tuning.SizeWeight).IsEqualTo(2);
        await Assert.That(tuning.SpeedWeight).IsEqualTo(3);
        await Assert.That(tuning.Lookahead).IsEqualTo(1);
        await Assert.That(tuning.FrequencyBase).IsEqualTo(4);
        await Assert.That(tuning.ChooseConventions).IsTrue();
    }

    [Test]
    public async Task Parse_AppliesOverABaseline()
    {
        var tuning = PlacementTuning.Parse("speed-weight=2", PlacementTuning.Size);

        await Assert.That(tuning.SizeWeight).IsEqualTo(8);
        await Assert.That(tuning.SpeedWeight).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_RejectsUnknownKnobs()
    {
        await Assert.That(() => PlacementTuning.Parse("nonsense=1")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Options_KeepTheDefaultsForUnsetKnobs()
    {
        var options = BeamPlacementOptions.From(new PlacementTuning { Lookahead = 1 });
        var defaults = new BeamPlacementOptions();

        await Assert.That(options.Lookahead).IsEqualTo(1);
        await Assert.That(options.BeamWidth).IsEqualTo(defaults.BeamWidth);
        await Assert.That(options.FrequencyBase).IsEqualTo(defaults.FrequencyBase);
    }
}
