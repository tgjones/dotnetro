namespace Irie.Mir;

public sealed class MirBlock
{
    /// <summary>
    /// Virtual-register IDs of block parameters (typed/classed vregs); empty
    /// on the entry block after ABI lowering and on all blocks after RA.
    /// </summary>
    public List<int> Parameters { get; } = [];

    /// <summary>
    /// Physical-register IDs known to be live on entry to this block.
    /// (1) On the entry block after ABI lowering: the calling convention live-ins.
    /// (2) On any block after RA: the physical registers live-in per the final
    ///     assignment.
    /// </summary>
    public List<int> LiveIns { get; } = [];

    public List<MirInstruction> Instructions { get; } = [];

    /// <summary>Populated by MirFunction.RebuildCfg from terminator BlockTarget operands.</summary>
    public List<MirBlock> Predecessors { get; } = [];
    public List<MirBlock> Successors   { get; } = [];

    public MirFunction? Parent { get; internal set; }

    public MirInstruction AddInstruction(OpcodeRef opcode, params MirOperand[] operands)
    {
        var instruction = new MirInstruction(opcode, operands);
        instruction.Parent = this;
        Instructions.Add(instruction);
        return instruction;
    }

    public MirInstruction InsertInstruction(int index, OpcodeRef opcode, params MirOperand[] operands)
    {
        var instruction = new MirInstruction(opcode, operands);
        instruction.Parent = this;
        Instructions.Insert(index, instruction);
        return instruction;
    }
}
