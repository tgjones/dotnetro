using Irie.Dialects.Call;
using Irie.Target.MOS6502;

namespace Irie.Tools.Exec;

/// <summary>
/// What irie-exec needs to know about a target: how to compile for it, which
/// emulator system runs the result, and how the generated driver prints a byte.
/// Names match iriec's --target.
/// </summary>
internal sealed record ExecTarget(
    Func<Irie.Target.Target> Create,
    string EmulatorSystem,
    int PutCharAddress,
    int PutCharConvention,
    int SoftStackPointerAddress,
    int SoftStackTop)
{
    /// <summary>
    /// Only the BBC Micro has an emulator system and a driver output routine so
    /// far (OSASCI, which the emulator's stub treats as "print A").
    /// </summary>
    private static readonly Dictionary<string, ExecTarget> Targets = new()
    {
        ["mos6502-bbcmicro"] = new(() => new MOS6502BbcMicroTarget(), "bbcmicro", 0xFFE3, CallConvention.BbcMicroOS, 0x00, 0x7C00),
    };

    public static ExecTarget Get(string name) =>
        Targets.TryGetValue(name, out var target)
            ? target
            : throw new ArgumentException(
                $"Unsupported --target '{name}' (irie-exec supports: {string.Join(", ", Targets.Keys)}).");
}
