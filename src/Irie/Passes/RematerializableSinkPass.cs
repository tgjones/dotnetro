using Irie.Mir;

namespace Irie.Passes;

// Sinks a rematerializable value's def into the single block that uses it, when
// that block is not the def block. This is the llvm-mos "Machine code sinking"
// (`machine-sink`) analogue, restricted to the trivially-rematerializable defs
// the register allocator already understands (constants, symbol-address bytes —
// see RegisterAllocatorPass.IsRematerializable).
//
// WHY. A rematerializable value defined in one block but used only on a subset
// of the paths leaving it stays live across the branch, tying up a scarce real
// register (`$a`/`$x`/`$y`) on every path — even the ones that never use it.
// That pressure forces the allocator to evict a longer-lived value (e.g. an
// argument) into the zero-page register file, costing a store at the def and a
// reload at each use. Moving the def down onto the path that actually needs it
// frees the register on the other paths. On the 6502 this is almost free:
// re-materializing a constant (`LDA #k`, 2 bytes) is the same size as reloading
// it (`LDA zp`) and skips the store — which is exactly why llvm-mos marks
// `LDImm` `isReMaterializable` and sinks it here.
//
// The canonical case is a guard clause (`if (a < 0) return 0;`): the constant 0
// return value is materialized in the entry block but used only on the taken
// arm. llvm-mos splits the critical edge in MachineSink to create a block to
// sink it into; Irie's post-ReturnMerge form already has that dedicated
// single-successor return block, so the constant sinks straight into it.
//
// SCOPE / SAFETY. We sink only when:
//   * the def defines exactly one virtual register and is rematerializable
//     (reads no registers, so it can be recomputed at any program point), and
//   * every use of that vreg lies in one block B other than the def block.
// A def whose uses span several blocks is left alone — spreading a
// materialization across blocks is duplication, which is the register
// allocator's job to price (via spill weights), not a sink's. We also refuse to
// sink into a block that sits on a cycle the def block does not (a loop the def
// is outside): that would recompute the value every iteration. This mirrors
// MachineSink's loop-depth profitability guard.
//
// Runs pre-RA, after SSA destruction (PhiElimination + TwoAddress) so all uses
// are ordinary operands in concrete blocks, and before RegisterAllocatorPass so
// the relieved pressure feeds the colourer. The move never changes the CFG (it
// relocates a non-terminator instruction), so no re-analysis is needed.
public sealed class RematerializableSinkPass : MirFunctionPass
{
    public override string Name => "RematerializableSink";

    public override void Run(MirFunction function)
    {
        // Successor lists drive the cycle guard; make sure they're current.
        function.RebuildCfg();

        foreach (var defBlock in function.Blocks.ToArray())
        {
            foreach (var def in defBlock.Instructions.ToArray())
            {
                if (!TryGetSoleVregDef(def, out var vreg)) continue;
                if (!RegisterAllocatorPass.IsRematerializable(def)) continue;

                var useBlock = SingleUseBlock(function, vreg);
                if (useBlock is null || useBlock == defBlock) continue;

                // Don't recompute a value once-per-iteration by sinking it into a
                // loop the def sits outside of.
                if (InCycle(useBlock) && !InCycle(defBlock)) continue;

                defBlock.Instructions.Remove(def);
                var firstUse = FirstUseIndex(useBlock, vreg);
                useBlock.InsertInstruction(firstUse, def.Opcode, def.Operands);
            }
        }
    }

    // A def usable here defines exactly one virtual register and no physical
    // register (so relocating it observes nothing but that one value).
    private static bool TryGetSoleVregDef(MirInstruction instr, out int vreg)
    {
        vreg = -1;
        foreach (var op in instr.Operands)
        {
            switch (op)
            {
                case PhysicalReg { IsDefinition: true }:
                    return false;
                case VirtualReg { IsDefinition: true } v:
                    if (vreg != -1) return false; // more than one vreg def
                    vreg = v.Id;
                    break;
            }
        }
        return vreg != -1;
    }

    // The unique block containing every use of `vreg`, or null if the uses are
    // spread across more than one block (or there are none).
    private static MirBlock? SingleUseBlock(MirFunction function, int vreg)
    {
        MirBlock? found = null;
        foreach (var block in function.Blocks)
        {
            if (!BlockUses(block, vreg)) continue;
            if (found is not null && found != block) return null;
            found = block;
        }
        return found;
    }

    private static bool BlockUses(MirBlock block, int vreg)
    {
        foreach (var instr in block.Instructions)
            if (InstructionUses(instr, vreg))
                return true;
        return false;
    }

    private static int FirstUseIndex(MirBlock block, int vreg)
    {
        for (var i = 0; i < block.Instructions.Count; i++)
            if (InstructionUses(block.Instructions[i], vreg))
                return i;
        return block.Instructions.Count;
    }

    private static bool InstructionUses(MirInstruction instr, int vreg)
    {
        foreach (var op in instr.Operands)
        {
            switch (op)
            {
                case VirtualReg { IsDefinition: false } v when v.Id == vreg:
                    return true;
                case BlockTarget bt when ArgsUse(bt.Args, vreg):
                    return true;
            }
        }
        return false;
    }

    private static bool ArgsUse(MirOperand[] args, int vreg)
    {
        foreach (var arg in args)
            if (arg is VirtualReg { IsDefinition: false } v && v.Id == vreg)
                return true;
        return false;
    }

    // True if `block` can reach itself along ≥1 CFG edge (it sits on a cycle).
    private static bool InCycle(MirBlock block)
    {
        var visited = new HashSet<MirBlock>();
        var stack = new Stack<MirBlock>(block.Successors);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == block) return true;
            if (!visited.Add(current)) continue;
            foreach (var succ in current.Successors)
                stack.Push(succ);
        }
        return false;
    }
}
