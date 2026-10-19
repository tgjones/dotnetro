using Irie.Mir;

namespace Irie.Target;

/// <summary>
/// MIR-shaped instruction selector. The InstructionSelectorPass walks each
/// function's instructions in order, asking the target's selector to lower
/// each one to target-specific opcodes. Mirrors the old
/// Irie.CodeGen.InstructionSelector, but talks in MirInstruction / MirBuilder.
/// </summary>
public abstract class InstructionSelector
{
    /// <summary>
    /// Called once per function so the selector can reset any per-function
    /// state (e.g. merge maps) before processing begins.
    /// </summary>
    public virtual void BeginFunction(MirFunction function) { }

    /// <summary>
    /// Called before the first instruction of each block, in block-list order
    /// (not CFG order), so per-block assumptions about machine state carried
    /// over from the previous block in the list must be dropped here.
    /// </summary>
    public virtual void BeginBlock(MirBlock block) { }

    /// <summary>
    /// Called once per function after its last instruction was selected, for
    /// clean-up of what selection left dead.
    /// </summary>
    public virtual void EndFunction(MirFunction function, MirBuilder builder) { }

    /// <summary>
    /// Select a single instruction. The implementation may emit replacement
    /// instructions via the builder and call builder.Remove(instruction)
    /// for the original. Returns true if the instruction was handled
    /// (including pass-through cases that need no change); false if
    /// the selector has no rule for this opcode.
    /// </summary>
    public abstract bool Select(MirInstruction instruction, MirBuilder builder);
}
