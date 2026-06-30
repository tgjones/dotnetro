using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// SsaRegisterAllocatorPass — SSA-based register allocation.
// =============================================================================
//
// References:
//   Hack et al. 2006, "Towards Register Allocation for Programs in SSA-form"
//   Braun & Hack 2009, "Register Allocation for Programs in SSA Form"
//
// Input:  post-isel, post-PhiElimination MIR in true SSA form — each vreg
//         defined exactly once, annotated with a ClassedVReg register class,
//         no block parameters or tied-operand copies (TwoAddressInstructionPass
//         has NOT run).
// Output: physreg-only MIR. Every VirtualReg operand replaced with PhysicalReg.
//         Block live-ins recomputed. Vreg annotation table cleared.
//
// Algorithm:
//   1. Pressure analysis and spilling (throws NotImplementedException when
//      needed — all current corpus tests fit within register pressure).
//   2. SSA coloring: process vregs in DFS-pre-order (RPO approximation), which
//      is a perfect elimination ordering for the chordal SSA interference graph.
//      Greedy coloring on a PEO always succeeds when pressure ≤ k (Hack 2006).
//   3. Tied-operand congruence classes: for each tied (def, use) pair, union
//      the two vregs; if they interfere, break by inserting a pseudo.copy.
//      All members of a surviving class share a single color.
//   4. Apply the assignment and recompute block live-ins.
//
// Enabled by IRIE_SSA_RA=1; replaces TwoAddressInstructionPass +
// (optional) RegisterCoalescerPass + RegisterAllocatorPass.
public sealed class SsaRegisterAllocatorPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "SsaRegisterAllocator";

    public override void Run(MirFunction function)
    {
        // Build congruence classes first (may insert pseudo.copy for interfering
        // tied pairs), then re-compute intervals on the possibly-mutated IR.
        var intervals = new LiveIntervalsAnalysis().Compute(function);
        var (uf, copiesInserted) = BuildCongruenceClasses(function, intervals);
        if (copiesInserted)
            intervals = new LiveIntervalsAnalysis().Compute(function);

        // Collect vregs in definition order — RPO block traversal gives a
        // suitable approximation of the dominator-tree pre-order PEO.
        var vregs = CollectVregsInDefOrder(function);

        // For each congruence class, allowed colors = intersection of all members'
        // class sets. Singleton classes use their own class set directly.
        var classAllowed = ComputeClassAllowedColors(function, vregs, uf);

        // Greedy SSA coloring.
        var assignment = Color(vregs, intervals, uf, classAllowed);

        // Apply assignment and recompute block live-ins (identical to the
        // graph-colouring allocator's post-colouring step).
        ApplyAssignment(function, assignment);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, intervals, assignment);
    }

    // =========================================================================
    // Tied-operand congruence classes (algorithm step 3)
    // =========================================================================
    //
    // An instruction's TiedOperands[usePos] = defPos means the use vreg at
    // usePos must end up in the same register as the def vreg at defPos (the
    // two-address constraint). We represent this as a union-find congruence
    // class so the coloring step can assign one color to all members:
    //
    //   - If the tied (def, use) vregs do NOT interfere → union them; they can
    //     share a register without a copy.
    //   - If they DO interfere → break the pair: insert `fresh = pseudo.copy use`
    //     before the instruction, redirect the tied use to `fresh`, and union
    //     `(def, fresh)` instead. The fresh vreg's interval is tiny (only live
    //     from the copy to the instruction) and never interferes with the def.
    private (UnionFind Uf, bool CopiesInserted) BuildCongruenceClasses(
        MirFunction function, LiveIntervals intervals)
    {
        var uf = new UnionFind();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is VirtualReg v)
                        uf.MakeSet(v.Id);

        var copiesInserted = false;

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                var tied = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).TiedOperands;
                if (tied == null) continue;

                for (var usePos = 0; usePos < tied.Length; usePos++)
                {
                    var defPos = tied[usePos];
                    if (defPos < 0) continue;

                    if (instr.Operands[defPos] is not VirtualReg defOp || !defOp.IsDefinition) continue;
                    if (instr.Operands[usePos] is not VirtualReg useOp || useOp.IsDefinition) continue;

                    var defV = defOp.Id;
                    var useV = useOp.Id;

                    if (intervals.Interfere(defV, useV))
                    {
                        // The endpoints interfere — insert a copy to break the pair.
                        // `fresh = pseudo.copy useV` immediately before the instruction;
                        // the instruction's tied operand becomes `fresh`. Union (defV, fresh)
                        // instead, since fresh's tiny interval cannot interfere with defV's.
                        var annotation = function.GetVRegAnnotation(useV) as ClassedVReg
                            ?? throw new InvalidOperationException(
                                $"SsaRegisterAllocator: vreg %{useV} has no register class.");
                        var fresh = function.CreateVirtualRegisterInClass(
                            annotation.ClassId, annotation.Name);
                        uf.MakeSet(fresh);

                        block.InsertInstruction(
                            i,
                            PseudoDialect.OpRef(PseudoOp.Copy),
                            new VirtualReg(fresh, IsDefinition: true),
                            new VirtualReg(useV, IsDefinition: false));
                        i++; // skip past the copy we just inserted
                        instr.Operands[usePos] = new VirtualReg(fresh, IsDefinition: false);
                        uf.Union(defV, fresh);
                        copiesInserted = true;
                    }
                    else
                    {
                        uf.Union(defV, useV);
                    }
                }
            }
        }

        return (uf, copiesInserted);
    }

    // =========================================================================
    // Collect vregs in definition order (RPO ≈ DFS pre-order / PEO)
    // =========================================================================
    private static List<int> CollectVregsInDefOrder(MirFunction function)
    {
        function.RebuildCfg();
        var rpo = ComputeReversePostOrder(function);
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var block in rpo)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is VirtualReg { IsDefinition: true } v && seen.Add(v.Id))
                        result.Add(v.Id);
        return result;
    }

    // =========================================================================
    // Per-congruence-class allowed color sets
    // =========================================================================
    //
    // For each congruence class the allowed set = intersection of the allocatable
    // register sets of all its members. A singleton class uses its own set directly.
    private Dictionary<int, int[]> ComputeClassAllowedColors(
        MirFunction function, IReadOnlyList<int> vregs, UnionFind uf)
    {
        var classAllowed = new Dictionary<int, int[]>();
        foreach (var vreg in vregs)
        {
            int classId;
            switch (function.GetVRegAnnotation(vreg))
            {
                case ClassedVReg classed:
                    classId = classed.ClassId;
                    break;
                // A vreg still carrying TypedVReg at RA flows only through pseudo.copy
                // and has no hard physreg constraint — assign the flexible 8-bit class
                // (same handling as RegisterAllocationSupport.ComputeAllowedColours).
                case TypedVReg typed when registerInfo.FlexibleI8ClassId != 0
                                         && typed.Type.SizeInBits <= 8:
                    classId = registerInfo.FlexibleI8ClassId;
                    function.ReclassifyVirtualRegister(
                        vreg, classId,
                        registerInfo.GetRegisterClassName(classId) ?? $"class{classId}");
                    break;
                case TypedVReg typed:
                    throw new InvalidOperationException(
                        $"SsaRegisterAllocator: vreg %{vreg} still has wide/unclassable type " +
                        $"{typed.Type.DisplayName} at RA — legalization/isel did not narrow it.");
                default:
                    throw new InvalidOperationException(
                        $"SsaRegisterAllocator: vreg %{vreg} has no register class annotation.");
            }
            var allowed = registerInfo.GetAllocatableRegisters(classId).ToArray();

            var root = uf.Find(vreg);
            if (!classAllowed.TryGetValue(root, out var existing))
                classAllowed[root] = allowed;
            else
                classAllowed[root] = Intersect(existing, allowed);
        }
        return classAllowed;
    }

    // =========================================================================
    // Greedy SSA coloring (algorithm step 2)
    // =========================================================================
    //
    // Process each vreg in definition order (PEO for the chordal SSA interference
    // graph). For each vreg:
    //   1. If its congruence class already has a color, use that color directly.
    //   2. Otherwise, start from the class's allowed set, remove physreg conflicts
    //      (physreg intervals that overlap this vreg's interval), remove colors
    //      taken by already-colored interfering vregs, pick the first remaining.
    //   3. Panic (NotImplementedException) if no color is available — spilling is
    //      not yet implemented.
    private static Dictionary<int, int> Color(
        IReadOnlyList<int> vregs,
        LiveIntervals intervals,
        UnionFind uf,
        Dictionary<int, int[]> classAllowed)
    {
        var assignment = new Dictionary<int, int>(); // vreg → physreg
        var classColor  = new Dictionary<int, int>(); // uf-root → assigned physreg

        foreach (var vreg in vregs)
        {
            var root = uf.Find(vreg);

            // If another member of the congruence class was already colored, reuse.
            if (classColor.TryGetValue(root, out var existingColor))
            {
                assignment[vreg] = existingColor;
                continue;
            }

            if (!classAllowed.TryGetValue(root, out var allowed) || allowed.Length == 0)
                throw new InvalidOperationException(
                    $"SsaRegisterAllocator: vreg %{vreg} (class root {root}) has an empty " +
                    "allowed color set — class intersection is empty.");

            // Start with the class's allowed set; remove physreg conflicts and
            // colors already taken by interfering neighbors.
            var available = new List<int>(allowed);

            // Physreg conflicts: a physreg whose live interval overlaps this vreg's interval.
            available.RemoveAll(physReg => intervals.Overlaps(vreg, physReg));

            // Neighbor conflicts: colors of already-colored vregs that interfere.
            foreach (var (other, otherColor) in assignment)
            {
                if (other == vreg) continue;
                if (intervals.Interfere(vreg, other))
                    available.Remove(otherColor);
            }

            if (available.Count == 0)
                throw new NotImplementedException(
                    $"SsaRegisterAllocator: vreg %{vreg} has no available color — " +
                    "spilling not yet implemented.");

            var color = available[0];
            assignment[vreg] = color;
            classColor[root]  = color; // record for congruence-class mates
        }

        return assignment;
    }

    // =========================================================================
    // Apply assignment: replace every VirtualReg operand with its PhysicalReg.
    // (Copied verbatim from RegisterAllocatorPass.)
    // =========================================================================
    private static void ApplyAssignment(MirFunction function, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                {
                    if (operands[i] is not VirtualReg v) continue;
                    if (!assignment.TryGetValue(v.Id, out var physReg))
                        throw new InvalidOperationException(
                            $"SsaRegisterAllocator: vreg %{v.Id} has no physical register assignment.");
                    operands[i] = new PhysicalReg(physReg, v.IsDefinition);
                }
            }
    }

    // =========================================================================
    // Recompute block live-ins after assignment.
    // (Copied verbatim from RegisterAllocatorPass.)
    // =========================================================================
    private static void RecomputeBlockLiveIns(
        MirFunction function, LiveIntervals intervals, IReadOnlyDictionary<int, int> assignment)
    {
        for (var i = 0; i < function.Blocks.Count; i++)
        {
            var block = function.Blocks[i];
            if (i == 0) continue;
            if (block.Instructions.Count == 0) { block.LiveIns.Clear(); continue; }

            var entryPoint = LiveIntervals.UseSlot(intervals.BaseSlotOf[block.Instructions[0]]);

            block.LiveIns.Clear();
            foreach (var vreg in assignment.Keys.OrderBy(v => v))
                if (intervals.IntervalOf(vreg).Covers(entryPoint)
                    && assignment.TryGetValue(vreg, out var physReg)
                    && !block.LiveIns.Contains(physReg))
                    block.LiveIns.Add(physReg);
        }
    }

    // =========================================================================
    // Reverse post-order traversal.
    // =========================================================================
    private static List<MirBlock> ComputeReversePostOrder(MirFunction function)
    {
        if (function.Blocks.Count == 0) return [];
        var visited = new HashSet<MirBlock>();
        var postOrder = new List<MirBlock>(function.Blocks.Count);

        void Dfs(MirBlock block)
        {
            if (!visited.Add(block)) return;
            foreach (var succ in block.Successors) Dfs(succ);
            postOrder.Add(block);
        }

        Dfs(function.Blocks[0]);
        foreach (var block in function.Blocks)
            if (!visited.Contains(block))
                postOrder.Add(block);
        postOrder.Reverse();
        return postOrder;
    }

    // =========================================================================
    // Union-Find for congruence classes.
    // =========================================================================
    private sealed class UnionFind
    {
        private readonly Dictionary<int, int> _parent = new();

        public void MakeSet(int id)
        {
            if (!_parent.ContainsKey(id))
                _parent[id] = id;
        }

        public int Find(int id)
        {
            if (!_parent.TryGetValue(id, out var parent))
                return id;
            if (parent == id) return id;
            var root = Find(parent);
            _parent[id] = root; // path compression
            return root;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }

    private static int[] Intersect(int[] a, int[] b)
    {
        var result = new List<int>(a.Length);
        foreach (var r in a)
        {
            foreach (var s in b)
                if (r == s) { result.Add(r); break; }
        }
        return result.ToArray();
    }
}
