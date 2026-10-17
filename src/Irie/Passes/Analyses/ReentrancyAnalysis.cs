using Irie.Mir;

namespace Irie.Passes.Analyses;

// Interprocedural analysis over a MirModule's call graph that determines, for
// each function, whether it is *non-reentrant* — i.e. it can never be active on
// the call stack more than once at a time.
//
// A non-reentrant function may place its locals in static memory (fixed
// zero-page addresses) rather than on a real call stack, because there is never
// a second live activation whose locals would alias the first's. The MOS6502
// static-frame allocator relies on this.
//
// Algorithm:
//   1. Build the directed call graph (see CallGraph): direct calls name their
//      callee, and an indirect call (including the lowered call of the runtime
//      trampoline) reaches every address-taken function — one referenced from a
//      vtable or other initializer, or whose address is materialised.
//   2. Tarjan SCCs. A function is non-reentrant iff:
//        * its SCC is a singleton (no mutual recursion), AND
//        * it does not call itself directly (no self-recursion), AND
//        * it is not reachable from an interrupt handler.
//      An interrupt handler can preempt and re-enter any function reachable
//      from it, which would alias static locals. No `[interrupt]` attribute
//      exists yet (see IsInterruptHandler below); when one is added, mark such
//      functions and the reachability filter here begins excluding their
//      transitive callees.
//
// The DotNetro corpus is entirely non-recursive, so every function qualifies
// today; the analysis exists so a future recursive program is rejected (left
// reentrant) rather than silently miscompiled.
public sealed class ReentrancyAnalysis
{
    // Runs the analysis over the module and writes the result onto each
    // MirFunction's IsNonReentrant flag.
    public static void Run(MirModule module)
    {
        var graph = CallGraph.Build(module);
        var functions = module.Functions;
        var callees = graph.Callees;

        var sccId = graph.SccId;
        var sccSize = graph.SccSize;

        // Interrupt reachability: functions reachable from an interrupt handler
        // can be preempted/re-entered. No interrupt attribute exists yet, so this
        // set is currently empty. Extension point: seed `interruptRoots` from
        // functions flagged with the future [interrupt] attribute and BFS the
        // call graph.
        var reachableFromInterrupt = ComputeInterruptReachability(functions, callees);

        for (var i = 0; i < functions.Count; i++)
        {
            functions[i].IsNonReentrant =
                sccSize[sccId[i]] == 1 &&
                !graph.SelfRecursive[i] &&
                !reachableFromInterrupt[i];
        }
    }

    // Extension point for interrupt handlers. No [interrupt] attribute exists in
    // the IR yet, so no function is currently an interrupt root and the returned
    // array is all-false. When the attribute lands, seed the roots here and BFS.
    private static bool[] ComputeInterruptReachability(
        List<MirFunction> functions, HashSet<int>[] callees)
    {
        var reachable = new bool[functions.Count];

        var roots = new List<int>();
        for (var i = 0; i < functions.Count; i++)
            if (IsInterruptHandler(functions[i]))
                roots.Add(i);

        var queue = new Queue<int>(roots);
        foreach (var r in roots)
            reachable[r] = true;

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var c in callees[i])
            {
                if (reachable[c]) continue;
                reachable[c] = true;
                queue.Enqueue(c);
            }
        }

        return reachable;
    }

    // Placeholder: no interrupt attribute exists in the MIR yet. Always false.
    // When an [interrupt] attribute is added to MirFunction, test it here.
    private static bool IsInterruptHandler(MirFunction function) => false;
}
