using Irie.Mir;

namespace Irie.Target.MOS6502;

/// <summary>
/// Backward liveness of physical registers (the homes included) over a function
/// that has been through placement: an operand marked as a definition ends the
/// register's live range, one that is not starts it.
/// </summary>
internal static class MOS6502PhysicalLiveness
{
    /// <summary>live_before = (live_after \ defs) ∪ uses.</summary>
    public static void Step(MirInstruction instr, HashSet<int> live)
    {
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: true } d)
                live.Remove(d.Id);
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: false } u)
                live.Add(u.Id);
    }

    /// <summary>
    /// To a fixpoint; returns each block's live-in and live-out. The function's CFG
    /// must be current (`RebuildCfg`).
    /// </summary>
    public static (Dictionary<MirBlock, HashSet<int>> LiveIn, Dictionary<MirBlock, HashSet<int>> LiveOut)
        Compute(MirFunction function)
    {
        var liveIn = function.Blocks.ToDictionary(b => b, _ => new HashSet<int>());
        var liveOut = function.Blocks.ToDictionary(b => b, _ => new HashSet<int>());
        bool changed;
        do
        {
            changed = false;
            for (var bi = function.Blocks.Count - 1; bi >= 0; bi--)
            {
                var block = function.Blocks[bi];
                var live = new HashSet<int>();
                foreach (var succ in block.Successors)
                    live.UnionWith(liveIn[succ]);
                liveOut[block] = new HashSet<int>(live);
                for (var i = block.Instructions.Count - 1; i >= 0; i--)
                    Step(block.Instructions[i], live);
                if (!live.SetEquals(liveIn[block]))
                {
                    liveIn[block] = live;
                    changed = true;
                }
            }
        } while (changed);
        return (liveIn, liveOut);
    }
}
