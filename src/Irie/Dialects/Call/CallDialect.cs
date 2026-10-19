using Irie.Mir;

namespace Irie.Dialects.Call;

/// <summary>
/// Calls: call.func (direct, by symbol) and call.indirect (through a ptr), each with an
/// optional calling convention (CallConvention) that Target.GetCallLowering resolves.
/// GenericMirVerifierPass checks the arguments and results of a call.func against the
/// callee's signature when the callee is in the module.
/// </summary>
public sealed class CallDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "call";

    public static OpcodeRef OpRef(CallOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((CallOp)code) switch
    {
        CallOp.Func     => "func",
        CallOp.Indirect => "indirect",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown call opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "func":     code = (ushort)CallOp.Func;     return true;
            case "indirect": code = (ushort)CallOp.Indirect; return true;
        }
        code = 0;
        return false;
    }

    /// <summary>
    /// The optional calling-convention immediate is use 1 (right after the callee
    /// symbol / target pointer); render it by name.
    /// </summary>
    public override bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        text = string.Empty;
        return useIndex == 1 && CallConvention.TryGetName(value, out text);
    }

    public override bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        value = 0;
        return useIndex == 1 && CallConvention.TryFromName(text, out value);
    }

    public override bool IsLegalImmediateOperand(ushort code, int useIndex) => useIndex == 1;

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    /// <summary>call ops have side effects (the callee is observable).</summary>
    public override bool IsSideEffectFree(ushort code) => false;

    /// <summary>Calls return to the next instruction; not terminators.</summary>
    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
