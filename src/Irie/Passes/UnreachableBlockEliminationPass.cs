using Irie.Mir;

namespace Irie.Passes;

/// <summary>
/// Removes the blocks that cannot be reached from the entry block, on generic SSA MIR.
/// </summary>
/// <remarks>
/// Folding a branch (<see cref="ConstantBranchFoldPass"/>) drops an edge and can leave the block behind it, and
/// everything only that block reached, without a way in. Such a block is dead code that would still be
/// selected, placed and emitted. This is LLVM's <c>-unreachableblockelim</c>.
///
/// Removing a block removes only its own outgoing edges, so every remaining edge still passes the arguments its
/// target's parameters expect. A block that stays reachable but has lost an incoming edge keeps its parameters;
/// <see cref="SinglePredecessorBlockParamsPass"/> folds them when one edge is left.
/// </remarks>
public sealed class UnreachableBlockEliminationPass : MirFunctionPass
{
    public override string Name => "UnreachableBlockElimination";

    public override void Run(MirFunction function)
    {
        var reachable = new HashSet<MirBlock>();
        var pending = new Stack<MirBlock>();
        pending.Push(function.Blocks[0]);
        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!reachable.Add(block))
                continue;
            foreach (var instruction in block.Instructions)
                foreach (var operand in instruction.Operands)
                    if (operand is BlockTarget target)
                        pending.Push(target.Block);
        }

        if (function.Blocks.RemoveAll(block => !reachable.Contains(block)) > 0)
            function.RebuildCfg();
    }
}
