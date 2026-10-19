using Irie.Mir;

namespace Irie.Passes;

public sealed class CompilationContext(MirModule module)
{
    public MirModule Module { get; } = module;

    /// <summary>Numbers a pass reports about its result, by name (`--stats` prints them).</summary>
    public Dictionary<string, long> Statistics { get; } = [];
}
