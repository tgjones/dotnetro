using Irie.Mir;

namespace Irie.Passes.Analyses;

// Natural loops of a function and the estimated execution frequency of each
// block.
//
// A back edge is an edge whose target dominates its source; the loop it closes
// is its header plus every block that reaches the source without passing through
// the header. Back edges to the same header form one loop. Edges that retreat to
// a block that does not dominate them (irreducible control flow) are not loops
// here, and the blocks involved get no extra depth.
//
// Frequency is relative to one execution of the function: 8^depth, capped, the
// usual static estimate when no branch probabilities are known.
public sealed class LoopInfo
{
    private readonly Dictionary<MirBlock, Loop> _innermost;

    public IReadOnlyList<Loop> Loops { get; }

    internal LoopInfo(IReadOnlyList<Loop> loops, Dictionary<MirBlock, Loop> innermost)
    {
        Loops = loops;
        _innermost = innermost;
    }

    // The smallest loop containing the block, or null outside every loop.
    public Loop? InnermostLoop(MirBlock block) => _innermost.GetValueOrDefault(block);

    // Number of loops containing the block (0 outside every loop).
    public int Depth(MirBlock block) => InnermostLoop(block)?.Depth ?? 0;

    // Loop depth beyond which the frequency stops growing: 8^6 is already far
    // past any real nesting, and keeps cost sums well inside a long.
    public const int MaxFrequencyDepth = 6;

    public long Frequency(MirBlock block)
    {
        var frequency = 1L;
        for (var i = Math.Min(Depth(block), MaxFrequencyDepth); i > 0; i--)
            frequency *= 8;
        return frequency;
    }
}

public sealed class Loop
{
    public required MirBlock Header { get; init; }

    // Every block of the loop, the header and the blocks of nested loops included.
    public required IReadOnlySet<MirBlock> Blocks { get; init; }

    // The blocks inside the loop with an edge back to the header.
    public required IReadOnlyList<MirBlock> Latches { get; init; }

    // The smallest loop that strictly contains this one, or null.
    public Loop? Parent { get; internal set; }

    // 1 for an outermost loop.
    public int Depth { get; internal set; }
}

public sealed class LoopAnalysis : MirFunctionAnalysis<LoopInfo>
{
    public override LoopInfo Compute(MirFunction function)
    {
        function.RebuildCfg();
        if (function.Blocks.Count == 0)
            return new LoopInfo([], []);

        var order = ReversePostOrder(function.Blocks[0]);
        var dominators = ComputeDominators(order);

        // Group the back edges by header.
        var latchesByHeader = new Dictionary<MirBlock, List<MirBlock>>();
        foreach (var block in order)
            foreach (var successor in block.Successors)
                if (Dominates(dominators, successor, block))
                {
                    if (!latchesByHeader.TryGetValue(successor, out var latches))
                        latchesByHeader[successor] = latches = [];
                    latches.Add(block);
                }

        var loops = new List<Loop>();
        foreach (var header in order)
        {
            if (!latchesByHeader.TryGetValue(header, out var latches))
                continue;

            // Walk the predecessors backwards from the latches, stopping at the header.
            var blocks = new HashSet<MirBlock> { header };
            var pending = new Stack<MirBlock>(latches);
            while (pending.Count > 0)
            {
                var block = pending.Pop();
                if (!blocks.Add(block))
                    continue;
                foreach (var predecessor in block.Predecessors)
                    if (dominators.ContainsKey(predecessor)) // reachable
                        pending.Push(predecessor);
            }

            loops.Add(new Loop { Header = header, Blocks = blocks, Latches = latches });
        }

        // Nesting: the parent is the smallest other loop containing the header.
        foreach (var loop in loops)
            foreach (var other in loops)
                if (other != loop && other.Blocks.Contains(loop.Header) && other.Blocks.Count > loop.Blocks.Count
                    && (loop.Parent is null || other.Blocks.Count < loop.Parent.Blocks.Count))
                    loop.Parent = other;
        foreach (var loop in loops)
        {
            loop.Depth = 1;
            for (var p = loop.Parent; p is not null; p = p.Parent)
                loop.Depth++;
        }

        var innermost = new Dictionary<MirBlock, Loop>();
        foreach (var loop in loops)
            foreach (var block in loop.Blocks)
                if (!innermost.TryGetValue(block, out var current) || loop.Depth > current.Depth)
                    innermost[block] = loop;

        return new LoopInfo(loops, innermost);
    }

    private static List<MirBlock> ReversePostOrder(MirBlock entry)
    {
        var visited = new HashSet<MirBlock> { entry };
        var postOrder = new List<MirBlock>();
        var stack = new Stack<(MirBlock Block, int Next)>();
        stack.Push((entry, 0));
        while (stack.Count > 0)
        {
            var (block, next) = stack.Pop();
            if (next < block.Successors.Count)
            {
                stack.Push((block, next + 1));
                var successor = block.Successors[next];
                if (visited.Add(successor))
                    stack.Push((successor, 0));
            }
            else
            {
                postOrder.Add(block);
            }
        }

        postOrder.Reverse();
        return postOrder;
    }

    // Immediate dominators (Cooper, Harvey, Kennedy), keyed by block; the entry
    // maps to itself. Unreachable blocks are absent.
    private static Dictionary<MirBlock, MirBlock> ComputeDominators(List<MirBlock> reversePostOrder)
    {
        var number = new Dictionary<MirBlock, int>();
        for (var i = 0; i < reversePostOrder.Count; i++)
            number[reversePostOrder[i]] = i;

        var idom = new Dictionary<MirBlock, MirBlock> { [reversePostOrder[0]] = reversePostOrder[0] };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in reversePostOrder.Skip(1))
            {
                MirBlock? candidate = null;
                foreach (var predecessor in block.Predecessors)
                {
                    if (!idom.ContainsKey(predecessor))
                        continue;
                    candidate = candidate is null ? predecessor : Intersect(idom, number, predecessor, candidate);
                }

                if (candidate is not null && (!idom.TryGetValue(block, out var current) || current != candidate))
                {
                    idom[block] = candidate;
                    changed = true;
                }
            }
        }

        return idom;
    }

    private static MirBlock Intersect(Dictionary<MirBlock, MirBlock> idom, Dictionary<MirBlock, int> number, MirBlock a, MirBlock b)
    {
        while (a != b)
        {
            while (number[a] > number[b]) a = idom[a];
            while (number[b] > number[a]) b = idom[b];
        }

        return a;
    }

    private static bool Dominates(Dictionary<MirBlock, MirBlock> idom, MirBlock dominator, MirBlock block)
    {
        if (!idom.ContainsKey(block) || !idom.ContainsKey(dominator))
            return false;
        while (true)
        {
            if (block == dominator)
                return true;
            var parent = idom[block];
            if (parent == block)
                return false;
            block = parent;
        }
    }
}
