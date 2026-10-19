namespace Irie.Target;

/// <summary>
/// The knobs of a target's placement search that a user can turn from the command
/// line; null leaves the target's own default. Each target interprets them in its
/// own placement pass.
/// </summary>
public sealed record PlacementTuning
{
    /// <summary>
    /// States the search keeps per operation; 1 is greedy, 0 keeps all of them (the
    /// oracle, bounded by the target's own state cap).
    /// </summary>
    public int? BeamWidth { get; init; }
    /// <summary>Whether a function that is only called directly picks its own convention.</summary>
    public bool ChooseConventions { get; init; }
    /// <summary>The objective is bytes * SizeWeight + cycles * SpeedWeight * block frequency.</summary>
    public int? SizeWeight { get; init; }
    public int? SpeedWeight { get; init; }
    /// <summary>Blocks ahead the exit state of a block is judged by.</summary>
    public int? Lookahead { get; init; }
    /// <summary>Block frequency is FrequencyBase^loop depth.</summary>
    public int? FrequencyBase { get; init; }

    public static PlacementTuning Size => new() { SizeWeight = 8, SpeedWeight = 1 };
    public static PlacementTuning Speed => new() { SizeWeight = 1, SpeedWeight = 8 };

    /// <summary>"beam=16,lookahead=2,size-weight=1,speed-weight=2,freq-base=8,choose-conventions".</summary>
    public static PlacementTuning Parse(string? spec, PlacementTuning? baseline = null)
    {
        var tuning = baseline ?? new PlacementTuning();
        if (string.IsNullOrWhiteSpace(spec)) return tuning;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair[0] == "choose-conventions" && pair.Length == 1)
            {
                tuning = tuning with { ChooseConventions = true };
                continue;
            }
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value) || value < 0)
                throw new ArgumentException($"Bad tuning '{part}'; expected name=number.");
            tuning = pair[0] switch
            {
                "beam" => tuning with { BeamWidth = value },
                "size-weight" => tuning with { SizeWeight = value },
                "speed-weight" => tuning with { SpeedWeight = value },
                "lookahead" => tuning with { Lookahead = value },
                "freq-base" => tuning with { FrequencyBase = Math.Max(1, value) },
                _ => throw new ArgumentException(
                    $"Unknown tuning '{pair[0]}' (beam, size-weight, speed-weight, lookahead, freq-base, choose-conventions)."),
            };
        }
        return tuning;
    }
}
