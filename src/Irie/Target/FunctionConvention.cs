namespace Irie.Target;

/// <summary>
/// Where a function takes its arguments and leaves its results: one physical register
/// per byte (arguments in parameter order, LSB first within a value; results likewise),
/// or Stack for an argument byte the caller places on the soft stack. A function that
/// has no convention of its own uses the target's default one, which depends only on
/// its signature.
/// </summary>
public sealed class FunctionConvention(IReadOnlyList<int> argBytes, IReadOnlyList<int> returnBytes)
{
    public const int Stack = -1;

    public IReadOnlyList<int> ArgBytes { get; } = argBytes;
    public IReadOnlyList<int> ReturnBytes { get; } = returnBytes;

    public bool SameAs(FunctionConvention other) =>
        ArgBytes.SequenceEqual(other.ArgBytes) && ReturnBytes.SequenceEqual(other.ReturnBytes);

    public override string ToString() =>
        $"args [{string.Join(", ", ArgBytes)}] -> [{string.Join(", ", ReturnBytes)}]";
}
