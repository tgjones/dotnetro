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
//   1. Constrained-operand isolation. A two-address (tied) operand pins its use
//      and def to one physical register (`$a` for adc/sbc on this chip). If a
//      value's WHOLE range were pinned to that single-register class it would
//      collide with every other value that also needs it (e.g. add-i16's two
//      addend high/low bytes both annotated `ac` and simultaneously live). So we
//      shorten each constrained range to the instruction itself:
//        a. Materialize every tied USE as a fresh copy right before the
//           instruction (`%u' = copy %u`; the instruction now reads %u'). The
//           original %u becomes copy-only, hence free to live anywhere flexible.
//        b. Relocate every tied DEF that outlives its instruction through a fresh
//           copy right after it (`%r = copy %d`; later uses read %r). %r is in the
//           flexible class, so the produced value escapes the single register.
//      After isolation the only `ac`-pinned ranges are the tiny ones spanning a
//      single instruction — they never overlap each other, so colouring on the
//      one-colour `ac` class always succeeds. This is the SSA analogue of
//      TwoAddressInstructionPass + InsertRelocationCopiesForConstrainedDefs; the
//      surrounding copies collapse to identities (removed by CopyElimination)
//      whenever the value could have stayed put.
//   2. SSA coloring: process vregs in DFS-pre-order (RPO approximation), which is
//      a perfect elimination ordering for the chordal SSA interference graph.
//      Greedy coloring on a PEO always succeeds when pressure ≤ k (Hack 2006).
//      Single-register (singleton-class) values are pre-coloured first — a
//      one-register constraint is a fact, not a choice — so the free values
//      colour around them via ordinary interference (the SSA register-targeting
//      decomposition: precoloured constraints + chordal colouring of the rest).
//      Candidate registers are tried copy-hint-first (so the isolation copies
//      collapse), then — for a relocation product — the scarce GPRs ahead of the
//      zero-page pool (so a brief cross-instruction value lands in `$y`/`$x` the
//      way llvm-mos does), then the value's class-default order.
//   3. Tied-operand congruence classes: each tied (def, use') pair is unioned so
//      every member shares one color (a no-op for single-register classes, where
//      both ends can only be that one register, but kept for broad-class ties).
//   4. Apply the assignment and recompute block live-ins.
//
// Enabled by IRIE_SSA_RA=1; replaces TwoAddressInstructionPass +
// (optional) RegisterCoalescerPass + RegisterAllocatorPass.
public sealed class SsaRegisterAllocatorPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "SsaRegisterAllocator";

    public override void Run(MirFunction function)
    {
        // Step 1: isolate constrained (tied) operands so no value's range is
        // pinned to a single-register class for longer than one instruction.
        // `relocationProducts` collects the fresh flexible temps that carry a
        // relocated def — colouring prefers a scarce GPR for these (short-lived
        // cross-instruction carriers), matching the llvm-mos `tay`/`tya` shape.
        var relocationProducts = new HashSet<int>();
        IsolateTiedUses(function);
        RelocateConstrainedLiveOutDefs(function, relocationProducts);

        // Congruence classes (tied def/use') then intervals on the final IR.
        var intervals = new LiveIntervalsAnalysis().Compute(function);
        var uf = BuildCongruenceClasses(function);

        // Collect vregs in definition order — RPO block traversal gives a
        // suitable approximation of the dominator-tree pre-order PEO.
        var vregs = CollectVregsInDefOrder(function);

        // Per-vreg allowed colours (with copy-only widening) then per-congruence-
        // class intersection.
        var perVregAllowed = RegisterAllocationSupport.ComputeAllowedColours(
            function, registerInfo, vregs);
        var classAllowed = ComputeClassAllowedColors(vregs, uf, perVregAllowed);

        // Greedy SSA coloring, copy-hint biased.
        var assignment = Color(function, vregs, intervals, uf, classAllowed, relocationProducts);

        // Apply assignment and recompute block live-ins (identical to the
        // graph-colouring allocator's post-colouring step).
        ApplyAssignment(function, assignment);
        function.ClearVRegAnnotations();
        RecomputeBlockLiveIns(function, intervals, assignment);
    }

    // =========================================================================
    // Step 1a: materialize every tied USE as a fresh copy (algorithm step 1a)
    // =========================================================================
    //
    // For each instruction with a tied (use → def) pair, insert
    // `%fresh = pseudo.copy %use` immediately before it and retie the use to
    // %fresh (in the def's register class — the class the operand must occupy).
    // The instruction now reads %fresh; the original %use feeds only this copy,
    // so it is copy-only and free to live anywhere flexible. The def and %fresh
    // span a single instruction and never overlap another constrained range.
    private static void IsolateTiedUses(MirFunction function)
    {
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
                    if (defOp.Id == useOp.Id) continue; // already self-tied

                    // The materialized use must occupy the def's register class
                    // (the two ends share a register); the def's annotation is the
                    // authoritative class for that operand.
                    var defAnnotation = function.GetVRegAnnotation(defOp.Id) as ClassedVReg
                        ?? throw new InvalidOperationException(
                            $"SsaRegisterAllocator: tied def %{defOp.Id} has no register class.");
                    var fresh = function.CreateVirtualRegisterInClass(
                        defAnnotation.ClassId, defAnnotation.Name);

                    block.InsertInstruction(
                        i,
                        PseudoDialect.OpRef(PseudoOp.Copy),
                        new VirtualReg(fresh, IsDefinition: true),
                        new VirtualReg(useOp.Id, IsDefinition: false));
                    i++; // skip past the copy we just inserted
                    instr.Operands[usePos] = new VirtualReg(fresh, IsDefinition: false);
                }
            }
        }
    }

    // =========================================================================
    // Step 1b: relocate every constrained tied DEF that outlives its instruction
    // =========================================================================
    //
    // After IsolateTiedUses a tied def is a pure definition (its tied use is now a
    // separate %fresh). If its value is read elsewhere it must vacate the single
    // register before the next chain link re-uses it. Insert `%r = pseudo.copy %d`
    // right after the defining instruction, redirect every other use of %d to %r,
    // and record %r as a relocation product. %r is in the flexible class so the
    // value can live in any free register. When the value could have stayed put
    // the copy colours to an identity and CopyElimination removes it.
    private void RelocateConstrainedLiveOutDefs(MirFunction function, HashSet<int> relocationProducts)
    {
        // Snapshot the tied-def sites first; we mutate instruction lists below.
        var defSites = new List<(MirBlock Block, MirInstruction Instr, int DefVreg)>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var tied = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).TiedOperands;
                if (tied == null) continue;

                for (var defPos = 0; defPos < instr.Operands.Length; defPos++)
                {
                    if (instr.Operands[defPos] is not VirtualReg defOp || !defOp.IsDefinition) continue;
                    if (!IsTiedDef(tied, defPos)) continue;
                    defSites.Add((block, instr, defOp.Id));
                }
            }

        var flexibleId = registerInfo.FlexibleI8ClassId;
        var flexibleName = flexibleId != 0
            ? registerInfo.GetRegisterClassName(flexibleId) ?? $"class{flexibleId}"
            : null;

        foreach (var (block, instr, defVreg) in defSites)
        {
            // Any use of defVreg outside its defining instruction?
            if (function.GetUseCount(defVreg) == 0) continue;

            // The value escapes its single-register class but the target advertises
            // no flexible class to hold it — there is nowhere to relocate it to.
            if (flexibleId == 0)
                throw new InvalidOperationException(
                    $"SsaRegisterAllocator: tied def %{defVreg} outlives its instruction and " +
                    $"must vacate its single-register class, but {registerInfo.GetType().Name} " +
                    "advertises no flexible register class (FlexibleI8ClassId == 0) to hold it.");

            var relocated = function.CreateVirtualRegisterInClass(flexibleId, flexibleName!);
            relocationProducts.Add(relocated);

            // Redirect every existing use of defVreg to the relocation temp, then
            // re-introduce the single use of defVreg in the relocation copy.
            function.ReplaceAllUsesOfRegister(defVreg, relocated);
            block.InsertInstruction(
                block.Instructions.IndexOf(instr) + 1,
                PseudoDialect.OpRef(PseudoOp.Copy),
                new VirtualReg(relocated, IsDefinition: true),
                new VirtualReg(defVreg, IsDefinition: false));
        }
    }

    private static bool IsTiedDef(int[] tiedOperands, int defIdx)
    {
        foreach (var t in tiedOperands)
            if (t == defIdx) return true;
        return false;
    }

    // =========================================================================
    // Tied-operand congruence classes (algorithm step 3)
    // =========================================================================
    //
    // After isolation every tied (def, use') pair is non-interfering (the use'
    // copy abuts the def). Union them so the colouring assigns one register to
    // both. For a single-register class this is a no-op (both can only be that
    // register); it matters only for a hypothetical broad-class tied operand.
    private static UnionFind BuildCongruenceClasses(MirFunction function)
    {
        var uf = new UnionFind();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is VirtualReg v)
                        uf.MakeSet(v.Id);

        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var tied = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).TiedOperands;
                if (tied == null) continue;

                for (var usePos = 0; usePos < tied.Length; usePos++)
                {
                    var defPos = tied[usePos];
                    if (defPos < 0) continue;
                    if (instr.Operands[defPos] is not VirtualReg defOp || !defOp.IsDefinition) continue;
                    if (instr.Operands[usePos] is not VirtualReg useOp || useOp.IsDefinition) continue;
                    uf.Union(defOp.Id, useOp.Id);
                }
            }

        return uf;
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
    // Start from each vreg's (already copy-only-widened) allowed set and intersect
    // the sets of all members of its congruence class. The first member's order is
    // preserved so colour preference (annotation-register-first widening) survives.
    private static Dictionary<int, int[]> ComputeClassAllowedColors(
        IReadOnlyList<int> vregs, UnionFind uf, IReadOnlyDictionary<int, int[]> perVregAllowed)
    {
        var classAllowed = new Dictionary<int, int[]>();
        foreach (var vreg in vregs)
        {
            var allowed = perVregAllowed[vreg];
            var root = uf.Find(vreg);
            if (!classAllowed.TryGetValue(root, out var existing))
                classAllowed[root] = allowed;
            else
                classAllowed[root] = Intersect(existing, allowed);
        }
        return classAllowed;
    }

    // =========================================================================
    // Greedy SSA coloring (algorithm step 2), copy-hint biased
    // =========================================================================
    private Dictionary<int, int> Color(
        MirFunction function,
        IReadOnlyList<int> vregs,
        LiveIntervals intervals,
        UnionFind uf,
        Dictionary<int, int[]> classAllowed,
        IReadOnlySet<int> relocationProducts)
    {
        var assignment = new Dictionary<int, int>(); // vreg → physreg
        var classColor  = new Dictionary<int, int>(); // uf-root → assigned physreg
        var shortRangeGprs = registerInfo.GetShortRangeGprPreference();

        // Pre-colour single-register (singleton-class) values before greedy
        // colouring. A length-1 allowed set is not a colouring choice — the value
        // has exactly one legal register — so we fix it up front and let the free
        // values colour around it through the ordinary neighbour check. After
        // constraint isolation each singleton range spans a single instruction and
        // overlaps no other singleton range, so this is always self-consistent.
        PreColorSingletonClasses(vregs, intervals, uf, classAllowed, assignment, classColor);

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

            int? chosen = null;
            foreach (var candidate in PreferenceOrder(
                         function, vreg, root, uf, allowed, assignment, relocationProducts, shortRangeGprs))
            {
                if (intervals.Overlaps(vreg, candidate)) continue;
                if (ConflictsWithColoredNeighbor(vreg, candidate, intervals, assignment)) continue;
                chosen = candidate;
                break;
            }

            if (chosen is null)
                throw new NotImplementedException(
                    $"SsaRegisterAllocator: vreg %{vreg} has no available color — " +
                    "spilling not yet implemented.");

            assignment[vreg] = chosen.Value;
            classColor[root]  = chosen.Value; // record for congruence-class mates
        }

        return assignment;
    }

    // Candidate colours in preference order: copy-hint colours first (so an
    // isolation copy collapses to an identity), then — for a relocation product —
    // the scarce GPRs ($x/$y/$a) ahead of the zero-page pool, then the class
    // allowed order. Mirrors GreedyRegisterAllocator.PreferenceOrder.
    private static IEnumerable<int> PreferenceOrder(
        MirFunction function, int vreg, int root, UnionFind uf, int[] allowed,
        IReadOnlyDictionary<int, int> assignment, IReadOnlySet<int> relocationProducts,
        ReadOnlySpan<int> shortRangeGprs)
    {
        var emitted = new HashSet<int>();
        var ordered = new List<int>();

        foreach (var hint in CopyHintColours(function, vreg, assignment))
            if (Contains(allowed, hint) && emitted.Add(hint))
                ordered.Add(hint);

        if (IsRelocationMember(uf, root, relocationProducts))
            foreach (var gpr in shortRangeGprs)
                if (Contains(allowed, gpr) && emitted.Add(gpr))
                    ordered.Add(gpr);

        foreach (var c in allowed)
            if (emitted.Add(c))
                ordered.Add(c);

        return ordered;
    }

    // True if any member of the congruence class is a relocation product.
    private static bool IsRelocationMember(UnionFind uf, int root, IReadOnlySet<int> relocationProducts)
    {
        foreach (var v in relocationProducts)
            if (uf.Find(v) == root) return true;
        return false;
    }

    // The already-known colours at the other end of every pseudo.copy this vreg
    // takes part in: a physreg operand directly, or an already-assigned vreg.
    private static IEnumerable<int> CopyHintColours(
        MirFunction function, int vreg, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
                if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
                if (instr.Operands.Length != 2) continue;

                var a = instr.Operands[0];
                var b = instr.Operands[1];
                var other = OperandIsVreg(a, vreg) ? b : OperandIsVreg(b, vreg) ? a : null;
                if (other is null) continue;

                switch (other)
                {
                    case PhysicalReg p:
                        yield return p.Id;
                        break;
                    case VirtualReg ov when assignment.TryGetValue(ov.Id, out var c):
                        yield return c;
                        break;
                }
            }
    }

    private static bool OperandIsVreg(MirOperand op, int vreg) =>
        op is VirtualReg v && v.Id == vreg;

    private static bool ConflictsWithColoredNeighbor(
        int vreg, int color, LiveIntervals intervals, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var (other, otherColor) in assignment)
        {
            if (other == vreg || otherColor != color) continue;
            if (intervals.Interfere(vreg, other)) return true;
        }
        return false;
    }

    // Assign every single-register (length-1 allowed set) congruence class its one
    // legal colour before greedy colouring runs. Populates `assignment` (so the
    // greedy loop's neighbour check sees these fixed registers and colours around
    // them) and `classColor` (so class mates reuse the colour). A singleton whose
    // register is already live across its range — another value pinned to the same
    // single-register class while simultaneously live, or a busy precoloured
    // window — is a genuinely unsatisfiable double-pin, surfaced loudly rather
    // than silently miscoloured.
    private static void PreColorSingletonClasses(
        IReadOnlyList<int> vregs, LiveIntervals intervals, UnionFind uf,
        Dictionary<int, int[]> classAllowed,
        Dictionary<int, int> assignment, Dictionary<int, int> classColor)
    {
        foreach (var vreg in vregs)
        {
            var root = uf.Find(vreg);
            if (!classAllowed.TryGetValue(root, out var allowed) || allowed.Length != 1) continue;
            var color = allowed[0];

            // A later class mate just reuses the colour the first member fixed.
            if (classColor.ContainsKey(root))
            {
                assignment[vreg] = color;
                continue;
            }

            if (intervals.Overlaps(vreg, color) ||
                ConflictsWithColoredNeighbor(vreg, color, intervals, assignment))
                throw new InvalidOperationException(
                    $"SsaRegisterAllocator: vreg %{vreg} is pinned to its only legal register " +
                    $"{color}, but that register is already live across its range — two values " +
                    "are constrained to one single-register class simultaneously " +
                    "(unsatisfiable without spilling).");

            assignment[vreg] = color;
            classColor[root] = color;
        }
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

    private static bool Contains(int[] values, int target)
    {
        foreach (var v in values)
            if (v == target) return true;
        return false;
    }
}
