using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

// Pure, target-agnostic helper that performs the bottom-up "static stack"
// colouring used to lay out the functions' zero-page blocks (homes and frame
// slots) in a fixed budget. It is NOT a wired pass — a target's layout pass
// constructs it, supplies the per-function size and the budget, and reads back
// each function's block base.
//
// === Static-stack colouring (the model) ===
//
// For a value held in a function's block to survive a call, the block must be
// DISJOINT from every transitive callee's block. We lay blocks out like a static
// stack, bottom-up:
//
//     base(f) = max over callees c of (base(c) + extent(c))      (leaf → 0)
//
// so f's block sits ABOVE all its callees' blocks. Independent subtrees of the
// call graph compute their bases independently and therefore reuse the same
// budget, so total budget use = the largest extent along any single call path,
// not the sum over all functions.
//
// Functions in one strongly connected component of the call graph (mutual or
// self recursion, or calls through a vtable that lead back) share one base and
// one extent (the largest member's size): they may overlap each other fully
// because every call inside the component saves the live bytes around itself
// (the target's recursion-save pass). From outside, the component is one node.
//
// The maths is entirely generic; the budget and the sizes come from the caller.
public sealed class StaticFrameColorer
{
    public sealed record Result(Dictionary<MirFunction, int> Base, List<MirFunction> Overflow);

    private readonly CallGraph _graph;
    private readonly Func<MirFunction, int> _size;
    private readonly int _budgetSize;
    private readonly Func<MirFunction, int>? _floor;

    // `floor`, when given, is the lowest offset a function's block may start at:
    // the extent of callees outside the module's call graph (hand-written library
    // routines) that are placed at offset 0, so every caller must sit above them.
    public StaticFrameColorer(
        CallGraph graph, Func<MirFunction, int> size, int budgetSize, Func<MirFunction, int>? floor = null)
    {
        _graph = graph;
        _size = size;
        _budgetSize = budgetSize;
        _floor = floor;
    }

    // Returns, for every function of non-zero size, its block base within the
    // budget, plus the functions whose block does not fit (base + extent beyond
    // the budget). An overflowing function still has a base; it is up to the
    // caller to place its bytes elsewhere.
    public Result Colour()
    {
        var functions = _graph.Functions;
        var callees = _graph.Callees;
        var size = new int[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            size[i] = _size(functions[i]);

        // Bottom-up over the SCCs (ascending id = callees first). Every member of
        // an SCC shares one base: above everything that any member can call outside
        // the SCC.
        var members = new List<int>[_graph.SccSize.Length];
        for (var i = 0; i < functions.Count; i++)
            (members[_graph.SccId[i]] ??= []).Add(i);

        var baseOf = new int[functions.Count];
        var extent = new int[functions.Count];
        foreach (var scc in members)
        {
            var maxBelow = 0;
            var maxSize = 0;
            foreach (var m in scc)
            {
                maxSize = Math.Max(maxSize, size[m]);
                if (_floor is not null)
                    maxBelow = Math.Max(maxBelow, _floor(functions[m]));
                foreach (var c in callees[m])
                    if (_graph.SccId[c] != _graph.SccId[m])
                        maxBelow = Math.Max(maxBelow, baseOf[c] + extent[c]);
            }
            foreach (var m in scc)
            {
                baseOf[m] = maxBelow;
                extent[m] = maxSize;
            }
        }

        var bases = new Dictionary<MirFunction, int>();
        var overflow = new List<MirFunction>();
        for (var i = 0; i < functions.Count; i++)
        {
            if (size[i] == 0) continue;
            bases[functions[i]] = baseOf[i];
            if (baseOf[i] + size[i] > _budgetSize)
                overflow.Add(functions[i]);
        }
        return new Result(bases, overflow);
    }
}
