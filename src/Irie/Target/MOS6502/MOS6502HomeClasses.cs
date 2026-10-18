using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Home classes: sets of values that share one memory home.
//
// Two values may share a home when they are never live at the same time. Every
// block parameter is joined with the arguments it receives, and every copy with its
// source, as far as that is possible: with no limit on the number of homes,
// sharing can never force a spill, and a branch whose argument already sits in the
// parameter's home needs no move at all (the phi webs of a loop counter or an
// accumulator collapse into one home).
//
// Interference is by value: a value is live from its definition to its last use,
// and a copy does not make its source and its result interfere, since they hold the
// same value. Candidates are tried in decreasing block frequency, so a loop's
// moves are removed before a cold path's.
internal sealed class HomeClasses
{
    private readonly Dictionary<int, int> _parent = [];
    // Interference between class representatives.
    private readonly Dictionary<int, HashSet<int>> _adjacent = [];

    // The representative of every value that shares its home with another; a value
    // not in the result has a home of its own. `excluded` names values that must
    // keep their own (those that live in a fixed register or are rebuilt).
    public static Dictionary<int, int> Build(
        MOS6502NaivePlacementPass.Placer placer, LoopInfo loops, Func<int, bool> excluded)
    {
        var classes = new HomeClasses();
        classes.BuildInterference(placer);
        classes.Coalesce(placer, loops, excluded);

        var result = new Dictionary<int, int>();
        foreach (var vreg in classes._parent.Keys)
        {
            var root = classes.Find(vreg);
            if (root != vreg) result[vreg] = root;
        }
        return result;
    }

    private int Find(int vreg)
    {
        var root = vreg;
        while (_parent.TryGetValue(root, out var up) && up != root) root = up;
        return root;
    }

    private HashSet<int> Adjacent(int vreg)
    {
        if (!_adjacent.TryGetValue(vreg, out var set)) _adjacent[vreg] = set = [];
        return set;
    }

    private void AddEdge(int a, int b)
    {
        if (a == b) return;
        Adjacent(a).Add(b);
        Adjacent(b).Add(a);
    }

    private static bool IsValueCopy(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy
        && instr.Operands[0] is VirtualReg { IsDefinition: true }
        && instr.Operands[1] is VirtualReg { IsDefinition: false };

    private void BuildInterference(MOS6502NaivePlacementPass.Placer placer)
    {
        foreach (var block in placer.Function.Blocks)
        {
            var live = new HashSet<int>(placer._liveOut[block]);
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                var defs = placer.Defs(instr).Select(d => d.Id).Where(d => placer._uses.GetValueOrDefault(d) > 0).ToList();
                var copySource = IsValueCopy(instr) ? ((VirtualReg)instr.Operands[1]).Id : -1;
                foreach (var def in defs)
                    foreach (var other in live)
                        if (other != copySource) AddEdge(def, other);

                live.ExceptWith(placer.Defs(instr).Select(d => d.Id));
                foreach (var use in placer.Uses(instr)) live.Add(use.Id);
            }

            // Everything live at the top of the block is defined together: the
            // parameters, and the values flowing through.
            foreach (var parameter in block.Parameters)
                if (live.Contains(parameter))
                    foreach (var other in live)
                        AddEdge(parameter, other);
        }
    }

    private void Coalesce(MOS6502NaivePlacementPass.Placer placer, LoopInfo loops, Func<int, bool> excluded)
    {
        var candidates = new List<(long Weight, int A, int B)>();

        bool Candidate(int vreg) => placer.NeedsHome(vreg) && !excluded(vreg);

        foreach (var block in placer.Function.Blocks)
        {
            var weight = loops.Frequency(block);
            foreach (var instr in block.Instructions)
            {
                if (IsValueCopy(instr))
                {
                    var (to, from) = (((VirtualReg)instr.Operands[0]).Id, ((VirtualReg)instr.Operands[1]).Id);
                    if (Candidate(to) && Candidate(from) && placer._uses.GetValueOrDefault(to) > 0)
                        candidates.Add((weight, to, from));
                }
                foreach (var operand in instr.Operands)
                {
                    if (operand is not BlockTarget target) continue;
                    var parameters = target.Block.Parameters;
                    for (var k = 0; k < parameters.Count && k < target.Args.Length; k++)
                        if (target.Args[k] is VirtualReg { IsDefinition: false } arg
                            && Candidate(arg.Id) && Candidate(parameters[k])
                            && placer._uses.GetValueOrDefault(parameters[k]) > 0)
                            candidates.Add((weight, parameters[k], arg.Id));
                }
            }
        }

        foreach (var (_, a, b) in candidates.OrderByDescending(c => c.Weight))
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra == rb || Adjacent(ra).Contains(rb)) continue;
            Merge(ra, rb);
        }
    }

    // Folds class rb into class ra.
    private void Merge(int ra, int rb)
    {
        _parent[rb] = ra;
        _parent.TryAdd(ra, ra);
        foreach (var neighbour in Adjacent(rb))
        {
            Adjacent(neighbour).Remove(rb);
            AddEdge(ra, neighbour);
        }
        _adjacent.Remove(rb);
    }
}
