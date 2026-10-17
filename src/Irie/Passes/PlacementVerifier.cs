using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Target;

namespace Irie.Passes;

// What an allocator says each register operand of the code it produced stands
// for, so that the placement can be checked without trusting the allocator's own
// bookkeeping.
public sealed class PlacementAnnotation
{
    // Per instruction, per operand: the id of the value that operand reads or
    // defines, or -1 for an operand that is not a tracked value (an immediate, a
    // block target, a register the ABI fixes). Values are named by whatever the
    // allocator started from (virtual register ids for the graph-colouring
    // allocator); the verifier only compares them for equality.
    public Dictionary<MirInstruction, int[]> OperandValues { get; } = [];

    // For a value that `pseudo.reload` defines: the spilled value it must read
    // back from its slot.
    public Dictionary<int, int> ReloadSources { get; } = [];

    // Snapshot of the value ids behind the virtual registers of `function`, taken
    // while the operands are still virtual.
    public static PlacementAnnotation FromVirtualRegisters(MirFunction function)
    {
        var annotation = new PlacementAnnotation();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
            {
                var values = new int[instruction.Operands.Length];
                for (var i = 0; i < values.Length; i++)
                    values[i] = instruction.Operands[i] is VirtualReg v ? v.Id : -1;
                annotation.OperandValues[instruction] = values;
            }

        return annotation;
    }
}

// Translation-validation check of a register assignment: replays the code and
// verifies that every instruction reads the value it was meant to read.
//
// The state at each program point maps each physical register, and each spill
// slot, to the value it currently holds. A definition of value `v` in register
// `R` sets R := v; any other definition of a register (a clobber, an implicit
// flag def, a call's caller-saved set) forgets what it held. Where control flow
// joins, only facts every predecessor agrees on survive (solved to a fixpoint
// over the CFG, loops included). A use of `v` in `R` is an error unless R holds
// `v`; a reload of a spilled value is an error unless its slot holds that value.
//
// This uses nothing from the allocator but the annotation, in particular not its
// live intervals or interference graph, so it holds for any allocator that can
// say which value each operand stands for.
public static class PlacementVerifier
{
    // Spill slots share the state's key space with registers; registers have
    // non-negative ids, so slot `s` is stored under -1 - s.
    private static int SlotKey(long slot) => -1 - (int)slot;

    public static void Verify(MirFunction function, TargetRegisterInfo registerInfo, PlacementAnnotation annotation)
    {
        if (function.Blocks.Count == 0)
            return;
        function.RebuildCfg();

        var order = ReversePostOrder(function.Blocks[0]);
        var entryState = new Dictionary<MirBlock, Dictionary<int, int>>();
        var exitState = new Dictionary<MirBlock, Dictionary<int, int>>();
        entryState[order[0]] = [];

        // Optimistic fixpoint: a block that has not been computed yet is "top"
        // (every fact holds) and drops out of the meet.
        bool changed;
        do
        {
            changed = false;
            foreach (var block in order)
            {
                var entry = block == order[0] ? [] : Meet(block, exitState);
                if (entry is null)
                    continue;
                entryState[block] = entry;

                var exit = new Dictionary<int, int>(entry);
                foreach (var instruction in block.Instructions)
                    Transfer(function, registerInfo, annotation, block, instruction, exit, report: false);

                if (!exitState.TryGetValue(block, out var previous) || !SameFacts(previous, exit))
                {
                    exitState[block] = exit;
                    changed = true;
                }
            }
        } while (changed);

        foreach (var block in order)
        {
            var state = new Dictionary<int, int>(entryState[block]);
            foreach (var instruction in block.Instructions)
                Transfer(function, registerInfo, annotation, block, instruction, state, report: true);
        }
    }

    private static Dictionary<int, int>? Meet(MirBlock block, Dictionary<MirBlock, Dictionary<int, int>> exitState)
    {
        Dictionary<int, int>? result = null;
        foreach (var predecessor in block.Predecessors)
        {
            if (!exitState.TryGetValue(predecessor, out var state))
                continue;
            if (result is null)
            {
                result = new Dictionary<int, int>(state);
                continue;
            }

            foreach (var key in result.Keys.ToList())
                if (!state.TryGetValue(key, out var value) || value != result[key])
                    result.Remove(key);
        }

        return result;
    }

    private static bool SameFacts(Dictionary<int, int> a, Dictionary<int, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static void Transfer(
        MirFunction function, TargetRegisterInfo registerInfo, PlacementAnnotation annotation,
        MirBlock block, MirInstruction instruction, Dictionary<int, int> state, bool report)
    {
        var operands = instruction.Operands;
        annotation.OperandValues.TryGetValue(instruction, out var values);
        var isSpill = IsPseudo(instruction, PseudoOp.Spill);
        var isReload = IsPseudo(instruction, PseudoOp.Reload);

        // Uses are read before any definition of the same instruction lands.
        for (var i = 0; i < operands.Length; i++)
        {
            if (operands[i] is not PhysicalReg { IsDefinition: false } use || values is null || values[i] < 0)
                continue;
            if (report && (!state.TryGetValue(use.Id, out var held) || held != values[i]))
                Fail(function, block, instruction,
                    $"reads value %{values[i]} from {registerInfo.GetRegisterName(use.Id)}, which " +
                    (state.ContainsKey(use.Id) ? $"holds %{held}" : "holds no known value") + " here");
        }

        if (isReload && operands.Length >= 2 && operands[1] is Immediate { Value: var reloadSlot }
            && values is not null && values[0] >= 0
            && annotation.ReloadSources.TryGetValue(values[0], out var source)
            && report && (!state.TryGetValue(SlotKey(reloadSlot), out var stored) || stored != source))
        {
            Fail(function, block, instruction,
                $"reloads spill slot {reloadSlot} as value %{source}, but the slot " +
                (state.ContainsKey(SlotKey(reloadSlot)) ? $"holds %{stored}" : "holds no known value") + " here");
        }

        // Definitions: forget everything clobbered, then record what was defined.
        // Clobbers are the instruction's explicit physical-register defs (implicit
        // ones included): a dialect's per-opcode default list can be wider than a
        // particular instance, which declares what its expansion really clobbers.
        foreach (var operand in operands)
            if (operand is PhysicalReg { IsDefinition: true } def)
                state.Remove(def.Id);
        for (var i = 0; i < operands.Length; i++)
            if (operands[i] is PhysicalReg { IsDefinition: true } def && values is not null && values[i] >= 0)
                state[def.Id] = values[i];

        if (isSpill && operands.Length >= 2 && operands[0] is Immediate { Value: var spillSlot }
            && values is not null && values[1] >= 0)
            state[SlotKey(spillSlot)] = values[1];
    }

    private static bool IsPseudo(MirInstruction instruction, PseudoOp op) =>
        instruction.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)instruction.Opcode.Code == op;

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
                if (visited.Add(block.Successors[next]))
                    stack.Push((block.Successors[next], 0));
            }
            else
            {
                postOrder.Add(block);
            }
        }

        postOrder.Reverse();
        return postOrder;
    }

    private static void Fail(MirFunction function, MirBlock block, MirInstruction instruction, string message)
    {
        var dialect = DialectRegistry.ById(instruction.Opcode.Dialect);
        throw new MirVerificationException(
            $"PlacementVerifier: in @{function.Name} bb{function.Blocks.IndexOf(block)}, " +
            $"{dialect.Prefix}.{dialect.GetOpName(instruction.Opcode.Code)} {message}");
    }
}
