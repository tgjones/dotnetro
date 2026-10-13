using Irie.MachineCode;
using Irie.Target;
using Irie.Target.MOS6502;

namespace DotNetro.Compiler;

/// <summary>
/// A system DotNetro can compile for.
/// </summary>
/// <param name="Id">Stable identifier used in requests and shared links (e.g. <c>bbcmicro</c>).</param>
/// <param name="DisplayName">Name shown to users.</param>
/// <param name="EmulatorKind">Which emulator runs the produced image (e.g. <c>jsbeeb</c>).</param>
/// <param name="CreateTarget">Creates the Irie target (fresh per compile).</param>
/// <param name="Encode">Encodes machine code to raw bytes loaded at the given origin.</param>
/// <param name="WriteAssembly">Writes machine code as assembly text.</param>
public sealed record TargetDescriptor(
    string Id,
    string DisplayName,
    string EmulatorKind,
    Func<Target> CreateTarget,
    Func<MachineCodeModule, int, byte[]> Encode,
    Action<MachineCodeModule, TextWriter> WriteAssembly);

public static class TargetRegistry
{
    public const string BbcMicroId = "bbcmicro";

    private static readonly TargetDescriptor[] s_targets =
    [
        new TargetDescriptor(
            BbcMicroId,
            "BBC Micro",
            EmulatorKind: "jsbeeb",
            CreateTarget: () => new MOS6502BbcMicroTarget(),
            Encode: (module, origin) => new MOS6502BinaryEncoder().Encode(module, origin),
            WriteAssembly: MOS6502AssemblyWriter.Write),
    ];

    public static IReadOnlyList<TargetDescriptor> Targets => s_targets;

    public static TargetDescriptor Get(string id)
        => TryGet(id) ?? throw new ArgumentException($"Unknown target '{id}'.", nameof(id));

    public static TargetDescriptor? TryGet(string id)
        => s_targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
