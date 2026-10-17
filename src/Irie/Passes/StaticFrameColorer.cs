using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

// Pure, target-agnostic helper that performs the bottom-up "static stack"
// colouring used to lay out non-reentrant functions' frames in a fixed budget
// (e.g. a zero-page window). It is NOT a wired pass — a target's placement pass
// constructs it, supplies the policy (eligibility, per-function footprint,
// budget size), and reads back each promoted function's frame base.
//
// === Static-stack colouring (the model) ===
//
// For a value held in a frame slot to survive a call, a function's frame must be
// DISJOINT from every transitive callee's frame. We lay frames out like a static
// stack, bottom-up:
//
//     base(f)      = max over direct callees c of (base(c) + footprint(c))   (leaf → 0)
//     footprint(f) = perFunctionFootprint(f)  if f is eligible(f) AND
//                                                 base(f)+footprint(f) ≤ budget
//                    else 0
//
// so f's frame sits ABOVE all its callees' frames. Independent subtrees of the
// call graph compute their bases independently and therefore reuse the same
// budget, so total budget use = the largest frame footprint along any single
// call path, not the sum over all functions. A function whose footprint is 0
// (not promoted) reserves no budget, so it doesn't push its callers' frames up.
//
// The maths is entirely generic; the budget value and the eligibility/footprint
// policy come from the caller, so no zero-page or budget literal lives here.
public sealed class StaticFrameColorer
{
    private readonly CallGraph _graph;
    private readonly IReadOnlyList<MirFunction> _functions;
    private readonly Func<MirFunction, bool> _eligibility;
    private readonly Func<MirFunction, int> _perFunctionFootprint;
    private readonly int _budgetSize;

    public StaticFrameColorer(
        CallGraph graph,
        Func<MirFunction, bool> eligibility,
        Func<MirFunction, int> perFunctionFootprint,
        int budgetSize)
    {
        _graph = graph;
        _functions = graph.Functions;
        _eligibility = eligibility;
        _perFunctionFootprint = perFunctionFootprint;
        _budgetSize = budgetSize;
    }

    // Runs the colouring and returns, for each promoted function (footprint > 0),
    // a map from the function to its frame base within the budget. Functions not
    // present in the result stay in their default placement (the budget overflows,
    // or they are ineligible / empty).
    public Dictionary<MirFunction, int> Colour()
    {
        var callees = _graph.Callees;

        var eligible = new bool[_functions.Count];
        var size = new int[_functions.Count];
        for (var i = 0; i < _functions.Count; i++)
        {
            eligible[i] = _eligibility(_functions[i]);
            size[i] = _perFunctionFootprint(_functions[i]);
        }

        var footprint = new int[_functions.Count];
        var baseOf = new int[_functions.Count];

        // Bottom-up over the SCCs of the call graph (ascending id = callees first).
        // Every member of an SCC shares one base: above everything that any member
        // can call outside the SCC. Only a singleton SCC can be eligible, so a
        // cycle never claims budget itself but still pushes its callers above the
        // frames its members' callees use.
        var members = new List<int>[_graph.SccSize.Length];
        for (var i = 0; i < _functions.Count; i++)
            (members[_graph.SccId[i]] ??= []).Add(i);

        foreach (var scc in members)
        {
            var maxBelow = 0;
            foreach (var m in scc)
                foreach (var c in callees[m])
                    if (_graph.SccId[c] != _graph.SccId[m])
                        maxBelow = Math.Max(maxBelow, baseOf[c] + footprint[c]);
            foreach (var m in scc)
            {
                baseOf[m] = maxBelow;
                footprint[m] = scc.Count == 1 && eligible[m] && maxBelow + size[m] <= _budgetSize ? size[m] : 0;
            }
        }

        var result = new Dictionary<MirFunction, int>();
        for (var i = 0; i < _functions.Count; i++)
            if (footprint[i] > 0)
                result[_functions[i]] = baseOf[i];
        return result;
    }
}
