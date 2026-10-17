namespace Irie.MachineCode;

public sealed class MachineCodeFunction(string name)
{
    public string Name => name;
    public List<MachineCodeEntry> Body { get; } = [];

    // Bytes of zero page the routine uses above the ABI registers, starting at
    // the first byte past them (declared by `.zeropage N` in assembly). Program
    // layout keeps every caller's zero-page bytes clear of this range, so the
    // routine need not save what it overwrites.
    public int ZeroPageFootprint { get; set; }

    public MachineCodeLabel EmitLabel(string labelName)
    {
        var label = new MachineCodeLabel(labelName);
        Body.Add(label);
        return label;
    }

    public MachineCodeInstruction EmitInstruction(int opcode, params MachineCodeOperand[] operands)
    {
        var instruction = new MachineCodeInstruction(opcode, operands);
        Body.Add(instruction);
        return instruction;
    }
}
