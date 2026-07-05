using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// CopyPropagationPass — post-RA local copy propagation + dead-copy elimination
// over physreg-only pseudo.copy. The llvm-mos `machine-cp` (Machine Copy
// Propagation) analogue.
// =============================================================================
//
// Pipeline position: after PrologueEpilogueInsertion, before PseudoExpansion —
// the point at which every copy (RA's, the ABI's, and the prologue/epilogue
// shuttle copies PEI just inserted) is a uniform, side-effect-free
// `pseudo.copy $dst <- $src` on physical registers, but none have yet been
// lowered to concrete transfers (which DO clobber N/Z). Operating purely on
// pseudo.copy keeps the pass flag-agnostic.
//
// Why it exists: CopyEliminationPass runs *before* PEI and only removes identity
// copies ($a = copy $a). It therefore cannot clean up the copies PEI inserts.
// The motivating case is pressure/live-across-call: RA parks the return-value
// low byte in $y and copies it into $a for the rts, then MOS6502FrameLowering's
// $a↔$y preserve shuttle copies it straight back — a redundant round-trip:
//
//     $a = pseudo.copy $y     ; (RA)  A <- Y
//     $y = pseudo.copy $a     ; (PEI) Y <- A   — redundant: Y already == A
//     ...pla/sta restore loop (does not touch $y)...
//     $a = pseudo.copy $y     ; (PEI) A <- Y
//
// mirroring llvm-mos, we keep FrameLowering oblivious to register contents and
// fix this here in a general copy pass. Two per-block phases:
//
//   Phase A (forward) — remove a `$d = copy $s` whose destination already holds
//     the source's value (drops the redundant `$y = copy $a` above).
//   Phase B (backward) — remove a `$d = copy $s` whose destination is dead
//     (drops the now-dead `$a = copy $y`, since the following PLA redefines $a).
//
// Liveness reads defs/uses from operands AND the dialect's implicit def/use
// lists, so bare PEI ops such as `mos6502.pla` (implicit-def $a) are accounted
// for. Cross-block liveness uses the post-RA MirBlock.LiveIns convention.
public sealed class CopyPropagationPass(TargetRegisterInfo registerInfo) : MirFunctionPass
{
    public override string Name => "CopyPropagation";

    public override void Run(MirFunction function)
    {
        function.RebuildCfg();

        foreach (var block in function.Blocks)
        {
            RemoveRedundantCopies(block);
            RemoveDeadCopies(block);
        }
    }

    // --- Phase A: forward local copy propagation -----------------------------
    //
    // Track, per physreg, an integer "value token": two registers share a token
    // iff they provably hold the same value at this point. A `$d = copy $s` whose
    // $d already shares $s's token is redundant and removed. Any other definition
    // (operand def or dialect implicit def) assigns its register a fresh token.
    private static void RemoveRedundantCopies(MirBlock block)
    {
        var tokenOf = new Dictionary<int, int>();
        var nextToken = 0;
        int Fresh() => nextToken++;
        int TokenFor(int reg) =>
            tokenOf.TryGetValue(reg, out var t) ? t : tokenOf[reg] = Fresh();

        // Block live-ins each hold a distinct unknown value.
        foreach (var reg in block.LiveIns)
            tokenOf[reg] = Fresh();

        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instr = block.Instructions[i];

            if (TryGetPhysCopy(instr, out var def, out var use))
            {
                var srcToken = TokenFor(use);
                if (tokenOf.TryGetValue(def, out var dstToken) && dstToken == srcToken)
                {
                    block.Instructions.RemoveAt(i);
                    i--;
                    continue;
                }

                // $d now holds exactly $s's value.
                tokenOf[def] = srcToken;
                continue;
            }

            // A non-copy clobbers each of its defs with a fresh value.
            foreach (var d in Defs(instr))
                tokenOf[d] = Fresh();
        }
    }

    // --- Phase B: backward dead-copy elimination -----------------------------
    //
    // A `$d = copy $s` is dead when $d is not live immediately after it. Standard
    // backward liveness seeded with the block's live-out set.
    private void RemoveDeadCopies(MirBlock block)
    {
        var live = new HashSet<int>();
        foreach (var succ in block.Successors)
            live.UnionWith(succ.LiveIns);

        // A return block's callee-saved registers are live-out to the caller even
        // though no successor lists them; keep any copy that restores one. (The
        // return-value registers arrive via the return instruction's use operands
        // and are picked up in the scan below.)
        if (EndsInReturn(block))
            foreach (var reg in registerInfo.GetCalleeSavedRegisters())
                live.Add(reg);

        for (var i = block.Instructions.Count - 1; i >= 0; i--)
        {
            var instr = block.Instructions[i];

            if (TryGetPhysCopy(instr, out var def, out _) && !live.Contains(def))
            {
                block.Instructions.RemoveAt(i);
                continue;
            }

            // live_before = (live_after \ defs) ∪ uses
            foreach (var d in Defs(instr))
                live.Remove(d);
            foreach (var u in Uses(instr))
                live.Add(u);
        }
    }

    // A physreg→physreg pseudo.copy `$def = pseudo.copy $use`.
    private static bool TryGetPhysCopy(MirInstruction instr, out int def, out int use)
    {
        def = use = -1;
        if (instr.Opcode.Dialect != PseudoDialect.Id) return false;
        if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) return false;
        if (instr.Operands.Length != 2) return false;
        if (instr.Operands[0] is not PhysicalReg { IsDefinition: true } d) return false;
        if (instr.Operands[1] is not PhysicalReg { IsDefinition: false } u) return false;
        def = d.Id;
        use = u.Id;
        return true;
    }

    private static bool EndsInReturn(MirBlock block)
    {
        if (block.Instructions.Count == 0) return false;
        var last = block.Instructions[^1];
        return DialectRegistry.ById(last.Opcode.Dialect).IsReturn(last.Opcode.Code);
    }

    // Registers defined by an instruction: explicit physreg def operands plus the
    // dialect's implicit defs (e.g. mos6502.pla's $a).
    private static IEnumerable<int> Defs(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: true } p)
                yield return p.Id;
        var info = DialectRegistry.ById(instr.Opcode.Dialect).GetInstructionInfo(instr.Opcode.Code);
        if (info.ImplicitDefs is { } defs)
            foreach (var d in defs)
                yield return d;
    }

    // Registers used by an instruction: explicit physreg use operands plus the
    // dialect's implicit uses (e.g. mos6502.pha's $a).
    private static IEnumerable<int> Uses(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: false } p)
                yield return p.Id;
        var info = DialectRegistry.ById(instr.Opcode.Dialect).GetInstructionInfo(instr.Opcode.Code);
        if (info.ImplicitUses is { } uses)
            foreach (var u in uses)
                yield return u;
    }
}
