using Irie.Mir;

namespace Irie.Passes;

public sealed class CompilationContext(MirModule module)
{
    public MirModule Module { get; } = module;

    // Numbers a pass reports about its result, by name (`--stats` prints them).
    public Dictionary<string, long> Statistics { get; } = [];
}
