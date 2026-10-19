using Irie.Mir;

namespace Irie.Passes;

/// <summary>
/// Removes the parameters of blocks that have exactly one incoming edge, on
/// generic SSA MIR.
/// </summary>
/// <remarks>
/// A block parameter is a phi. With a single incoming edge it can only ever take
/// the one value that edge passes, so every use of the parameter is a use of that
/// argument — the LLVM `SimplifyCFG` / `InstCombine` single-entry-phi fold. The
/// argument is defined in the predecessor or dominates it, and the block's only
/// way in is through the predecessor, so it dominates every use of the parameter.
///
/// Without this the parameter would be a vreg of its own, copied to on the edge
/// by phi elimination and then coalesced (or not) by the allocator; the i32
/// compare ladders of C# code create many such blocks, which end up holding only
/// copies and a jump.
///
/// A block with two edges from the same predecessor (a `cf.cond_br` whose arms
/// both target it) has two incoming edges and is left alone.
/// </remarks>
public sealed class SinglePredecessorBlockParamsPass : MirFunctionPass
{
    public override string Name => "SinglePredecessorBlockParams";

    public override void Run(MirFunction function)
    {
        // Edges per target block, and the one edge when there is exactly one.
        var edgeCount = new Dictionary<MirBlock, int>();
        var edge = new Dictionary<MirBlock, BlockTarget>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                foreach (var operand in instruction.Operands)
                    if (operand is BlockTarget target)
                    {
                        edgeCount[target.Block] = edgeCount.GetValueOrDefault(target.Block) + 1;
                        edge[target.Block] = target;
                    }

        // Blocks in order: an argument that is itself a removed parameter has
        // already been rewritten by the time its block is reached, because the
        // predecessor was visited first or the rewrite reaches the argument too
        // (ReplaceAllUsesOfRegister also rewrites BlockTarget arguments).
        foreach (var block in function.Blocks)
        {
            if (block.Parameters.Count == 0 || edgeCount.GetValueOrDefault(block) != 1)
                continue;

            // Re-read the edge: earlier rewrites replace BlockTarget records.
            var target = FindEdge(function, block);
            if (target.Args.Length != block.Parameters.Count || target.Args.Any(a => a is not VirtualReg { IsDefinition: false }))
                continue;
            // A self-edge is the loop's own back edge, not an entry from outside.
            if (block.Instructions.Any(i => i.Operands.Any(o => o is BlockTarget t && t.Block == block)))
                continue;

            for (var i = 0; i < block.Parameters.Count; i++)
                function.ReplaceAllUsesOfRegister(block.Parameters[i], ((VirtualReg)target.Args[i]).Id);
            block.Parameters.Clear();
            ClearArguments(function, block);
        }

        function.RebuildCfg();
    }

    private static BlockTarget FindEdge(MirFunction function, MirBlock block)
    {
        foreach (var candidate in function.Blocks)
            foreach (var instruction in candidate.Instructions)
                foreach (var operand in instruction.Operands)
                    if (operand is BlockTarget target && target.Block == block)
                        return target;
        throw new InvalidOperationException("The block has no incoming edge.");
    }

    private static void ClearArguments(MirFunction function, MirBlock block)
    {
        foreach (var candidate in function.Blocks)
            foreach (var instruction in candidate.Instructions)
            {
                var operands = instruction.Operands;
                for (var i = 0; i < operands.Length; i++)
                    if (operands[i] is BlockTarget target && target.Block == block)
                        operands[i] = target with { Args = [] };
            }
    }
}
