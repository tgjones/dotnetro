using Irie.Mir;

namespace Irie.Dialects.Call;

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

    // The optional calling-convention immediate is use 1 (right after the callee
    // symbol / target pointer); render it by name.
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

    // call ops have side effects (the callee is observable).
    public override bool IsSideEffectFree(ushort code) => false;

    // Calls return to the next instruction; not terminators.
    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
