namespace Irie.Mir;

public sealed class MirInstruction(OpcodeRef opcode, MirOperand[] operands)
{
    public OpcodeRef Opcode { get; } = opcode;

    /// <summary>Defs first, then uses.</summary>
    public MirOperand[] Operands { get; internal set; } = operands;

    public MirBlock? Parent { get; internal set; }
}
