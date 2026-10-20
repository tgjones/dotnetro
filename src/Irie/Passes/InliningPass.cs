using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Cast;
using Irie.Dialects.Cf;
using Irie.Dialects.Core;
using Irie.Dialects.Mem;
using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Passes;

/// <summary>
/// Replaces direct calls (`call.func`) by a copy of the callee's body, on generic SSA
/// MIR, so the inlined code never pays argument and result marshalling.
/// </summary>
/// <remarks>
/// A callee is inlined when it is a plain generic function (no target ops, no
/// physical registers: the hand-written routines of the runtime are left alone), is
/// not part of a call-graph cycle, and either has exactly one call site and is never
/// address-taken (its body then moves rather than being duplicated), or is small
/// (<see cref="MaxSharedBodyCost"/> estimated bytes in at most
/// <see cref="MaxSharedBodyBlocks"/> blocks). Functions are visited callee-first, so
/// a callee has already absorbed its own small callees when it is measured.
///
/// Inlining one call splits the caller's block at the call. The head branches to the
/// cloned entry block (with the arguments as block arguments); each `core.return v`
/// of the clone becomes `cf.br cont(v)`, where `cont` holds the rest of the caller's
/// block and takes the call's result as its block parameter. Edges to a block with
/// no other predecessor are then merged away, so a straight-line callee is spliced
/// into the caller's block. Frame slots of the callee are cloned into the caller.
///
/// A function whose every call site was inlined and whose address is never taken is
/// removed from the module.
/// </remarks>
public sealed class InliningPass : Pass
{
    /// <summary>
    /// The most estimated bytes (<see cref="Weight"/>) a callee with several call sites may have. Measured over
    /// the lit programs: 3 shrinks them all, while 6 saves a little more in total but grows StringLength,
    /// whose four `get_Length` calls cost more inlined.
    /// </summary>
    internal const int MaxSharedBodyCost = 3;

    /// <summary>The most blocks a callee with several call sites may have.</summary>
    internal const int MaxSharedBodyBlocks = 3;

    private const string EntryFunction = "__entry";

    public override string Name => "Inlining";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        var graph = CallGraph.Build(module);
        var functions = module.Functions;

        var callSites = new int[functions.Count];
        foreach (var function in functions)
            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions)
                    if (TryGetDirectCallee(instruction, out var name)
                        && graph.IndexByName.TryGetValue(name, out var callee))
                        callSites[callee]++;

        // Callee-first: the SCC ids are assigned in Tarjan completion order.
        var order = Enumerable.Range(0, functions.Count).OrderBy(i => graph.SccId[i]).ToArray();
        var inlinedAway = new HashSet<int>();
        var inlinedCalls = 0;
        var slotCounter = 0;

        foreach (var callerIndex in order)
        {
            var caller = functions[callerIndex];
            var restart = true;
            while (restart)
            {
                restart = false;
                foreach (var block in caller.Blocks)
                {
                    foreach (var instruction in block.Instructions)
                    {
                        if (!TryGetDirectCallee(instruction, out var name)
                            || !graph.IndexByName.TryGetValue(name, out var calleeIndex)
                            || calleeIndex == callerIndex
                            || !ShouldInline(functions[calleeIndex], calleeIndex, graph, callSites))
                            continue;

                        Inline(caller, block, instruction, functions[calleeIndex], ref slotCounter);
                        inlinedAway.Add(calleeIndex);
                        inlinedCalls++;
                        restart = true;
                        break;
                    }
                    if (restart) break;
                }
            }
        }

        // Drop what was inlined everywhere and is referenced by nothing that stays.
        if (inlinedAway.Count > 0)
        {
            var addressTaken = CallGraph.Build(module).AddressTaken;
            var removable = inlinedAway.Where(i => !addressTaken[i]).Select(i => functions[i].Name).ToHashSet();
            bool changed;
            do
            {
                changed = false;
                foreach (var function in functions)
                {
                    if (removable.Contains(function.Name)) continue;
                    foreach (var block in function.Blocks)
                        foreach (var instruction in block.Instructions)
                            foreach (var operand in instruction.Operands)
                                if (operand is Symbol symbol && removable.Remove(symbol.Name))
                                    changed = true;
                }
            } while (changed);

            functions.RemoveAll(f => removable.Contains(f.Name));
            context.Statistics["inlining.functions-removed"] = removable.Count;
        }
        context.Statistics["inlining.calls-inlined"] = inlinedCalls;
    }

    private static bool ShouldInline(MirFunction callee, int calleeIndex, CallGraph graph, int[] callSites)
    {
        if (callee.Name == EntryFunction
            || callee.NoInline
            || graph.SccSize[graph.SccId[calleeIndex]] != 1
            || graph.SelfRecursive[calleeIndex]
            || callee.Convention is not null
            || callee.Blocks.Count == 0
            || !IsPlainGeneric(callee))
            return false;

        if (callee.AlwaysInline || callSites[calleeIndex] == 1 && !graph.AddressTaken[calleeIndex])
            return true;

        return callee.Blocks.Count <= MaxSharedBodyBlocks && LiveCost(callee) <= MaxSharedBodyCost;
    }

    /// <summary>Only generic dialect operations on typed vregs: nothing a target already lowered.</summary>
    private static bool IsPlainGeneric(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            if (block.LiveIns.Count > 0) return false;
            foreach (var instruction in block.Instructions)
            {
                var dialect = instruction.Opcode.Dialect;
                if (dialect != ArithDialect.Id && dialect != MemDialect.Id && dialect != CastDialect.Id
                    && dialect != CfDialect.Id && dialect != CoreDialect.Id && dialect != CallDialect.Id)
                    return false;
                foreach (var operand in instruction.Operands)
                    if (operand is PhysicalReg
                        || operand is VirtualReg v && function.GetVRegAnnotation(v.Id) is not TypedVReg)
                        return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The estimated size of the operations that survive removing dead side-effect-free ones
    /// (transitively), by <see cref="Weight"/>.
    /// </summary>
    private static int LiveCost(MirFunction function)
    {
        var uses = new Dictionary<int, int>();
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                foreach (var operand in instruction.Operands)
                    CountUses(operand, uses, 1);

        var dead = new HashSet<MirInstruction>();
        bool changed;
        do
        {
            changed = false;
            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions)
                {
                    if (dead.Contains(instruction)
                        || !DialectRegistry.ById(instruction.Opcode.Dialect).IsSideEffectFree(instruction.Opcode.Code))
                        continue;
                    var defs = instruction.Operands.OfType<VirtualReg>().Where(v => v.IsDefinition).ToList();
                    if (defs.Count == 0 || defs.Any(d => uses.GetValueOrDefault(d.Id) > 0)) continue;
                    dead.Add(instruction);
                    foreach (var operand in instruction.Operands)
                        CountUses(operand, uses, -1);
                    changed = true;
                }
        } while (changed);

        var cost = 0;
        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                if (!dead.Contains(instruction))
                    cost += Weight(function, instruction);
        return cost;
    }

    /// <summary>
    /// A rough size in 6502 bytes of one operation: about two bytes per byte of the widest value it works
    /// on, a call costs its JSR and the marshalling of its values, a branch on a condition its test and
    /// jump; constants, casts, symbols and unconditional branches cost nothing of their own.
    /// </summary>
    private static int Weight(MirFunction function, MirInstruction instruction)
    {
        var opcode = instruction.Opcode;
        if (opcode.Dialect == CastDialect.Id || opcode.Dialect == CoreDialect.Id)
            return 0;
        if (opcode.Dialect == CfDialect.Id)
            return (CfOp)opcode.Code == CfOp.CondBr ? 4 : 0;
        if (opcode.Dialect == ArithDialect.Id && (ArithOp)opcode.Code == ArithOp.Constant)
            return 0;
        if (opcode.Dialect == MemDialect.Id
            && (MemOp)opcode.Code is MemOp.Symbol or MemOp.FrameAddr)
            return 0;

        var widest = 1;
        var total = 0;
        foreach (var operand in instruction.Operands)
        {
            if (operand is not VirtualReg v || function.GetVRegAnnotation(v.Id) is not TypedVReg typed) continue;
            var bytes = Math.Max(1, (typed.Type.SizeInBits + 7) / 8);
            widest = Math.Max(widest, bytes);
            total += bytes;
        }
        return opcode.Dialect == CallDialect.Id ? 3 + total : 2 * widest;
    }

    private static void CountUses(MirOperand operand, Dictionary<int, int> uses, int delta)
    {
        switch (operand)
        {
            case VirtualReg { IsDefinition: false } v:
                uses[v.Id] = uses.GetValueOrDefault(v.Id) + delta;
                break;
            case BlockTarget target:
                foreach (var arg in target.Args)
                    CountUses(arg, uses, delta);
                break;
        }
    }

    private static bool TryGetDirectCallee(MirInstruction instruction, out string name)
    {
        name = string.Empty;
        if (instruction.Opcode.Dialect != CallDialect.Id || (CallOp)instruction.Opcode.Code != CallOp.Func)
            return false;
        // A call naming a calling convention (`call.func @f, bbcmicro_os, ...`) is a call of a ROM entry
        // point whatever the module defines under that name: it is not inlined.
        if (instruction.Operands.Any(o => o is Immediate))
            return false;
        foreach (var operand in instruction.Operands)
            if (operand is Symbol symbol)
            {
                name = symbol.Name;
                return true;
            }
        return false;
    }

    private static void Inline(
        MirFunction caller, MirBlock block, MirInstruction call, MirFunction callee, ref int slotCounter)
    {
        var operands = call.Operands;
        var defs = operands.TakeWhile(o => o is VirtualReg { IsDefinition: true }).Cast<VirtualReg>().ToList();
        var args = operands.Skip(defs.Count + 1).Where(o => o is not Immediate).ToArray();

        // Fresh vregs, blocks and frame slots for the clone.
        var vregMap = new Dictionary<int, int>();
        foreach (var id in callee.VirtualRegisterIds)
        {
            var typed = (TypedVReg)callee.GetVRegAnnotation(id);
            var fresh = caller.CreateVirtualRegister(typed.Type);
            caller.SetValueRange(fresh, typed.Range);
            vregMap[id] = fresh;
        }

        var slotMap = new Dictionary<int, int>();
        var nextSlotIndex = caller.FrameSlots.Count == 0 ? 0 : caller.FrameSlots.Max(s => s.Index) + 1;
        foreach (var slot in callee.FrameSlots)
        {
            var index = nextSlotIndex++;
            slotMap[slot.Index] = index;
            caller.FrameSlots.Add(new FrameSlot(index, slot.Type, $"{caller.Name}_inl{slotCounter++}_{slot.SymbolName}"));
        }

        var blockMap = new Dictionary<MirBlock, MirBlock>();
        foreach (var source in callee.Blocks)
        {
            var clone = new MirBlock { Parent = caller };
            foreach (var parameter in source.Parameters)
                clone.Parameters.Add(vregMap[parameter]);
            blockMap[source] = clone;
        }

        // The continuation: the rest of the caller's block, taking the result as its parameter.
        var continuation = new MirBlock { Parent = caller };
        var position = block.Instructions.IndexOf(call);
        for (var i = position + 1; i < block.Instructions.Count; i++)
        {
            block.Instructions[i].Parent = continuation;
            continuation.Instructions.Add(block.Instructions[i]);
        }
        block.Instructions.RemoveRange(position, block.Instructions.Count - position);
        foreach (var def in defs)
            continuation.Parameters.Add(def.Id);

        MirOperand Remap(MirOperand operand) => operand switch
        {
            VirtualReg v => v with { Id = vregMap[v.Id] },
            BlockTarget t => new BlockTarget(blockMap[t.Block], [.. t.Args.Select(Remap)]),
            _ => operand,
        };

        foreach (var source in callee.Blocks)
        {
            var clone = blockMap[source];
            foreach (var instruction in source.Instructions)
            {
                if (instruction.Opcode.Dialect == CoreDialect.Id && (CoreOp)instruction.Opcode.Code == CoreOp.Return)
                {
                    var returned = instruction.Operands.Select(Remap).ToArray();
                    clone.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(continuation, returned));
                    continue;
                }

                var copy = instruction.Operands.Select(Remap).ToArray();
                if (instruction.Opcode.Dialect == MemDialect.Id && (MemOp)instruction.Opcode.Code == MemOp.FrameAddr)
                    copy[1] = new Immediate(slotMap[(int)((Immediate)instruction.Operands[1]).Value]);
                clone.AddInstruction(instruction.Opcode, copy);
            }
        }

        block.AddInstruction(CfDialect.OpRef(CfOp.Br), new BlockTarget(blockMap[callee.Blocks[0]], args));

        // Lay the clone out right after the call's block, the continuation last.
        var at = caller.Blocks.IndexOf(block) + 1;
        caller.Blocks.InsertRange(at, callee.Blocks.Select(b => blockMap[b]));
        caller.Blocks.Insert(at + callee.Blocks.Count, continuation);
        caller.RebuildCfg();

        MergeSinglePredecessorEdges(caller, block, continuation, blockMap.Values);
    }

    /// <summary>
    /// Merges `A: ...; cf.br B(args)` into one block when B has no other predecessor,
    /// for the blocks this inlining created (the head and the continuation, and the
    /// clone's blocks reached from them).
    /// </summary>
    private static void MergeSinglePredecessorEdges(
        MirFunction caller, MirBlock head, MirBlock continuation, IEnumerable<MirBlock> clones)
    {
        var candidates = new List<MirBlock> { head };
        candidates.AddRange(clones);
        candidates.Add(continuation);

        foreach (var block in candidates)
        {
            while (block.Parent is not null && block.Instructions.Count > 0
                && block.Instructions[^1] is { } terminator
                && terminator.Opcode.Dialect == CfDialect.Id && (CfOp)terminator.Opcode.Code == CfOp.Br
                && terminator.Operands[0] is BlockTarget { } edge
                && edge.Block != block
                && edge.Block != caller.Blocks[0]
                && edge.Block.Predecessors.Count == 1
                && edge.Block.Parent is not null
                && edge.Args.Length == edge.Block.Parameters.Count
                && !SelfLoops(edge.Block))
            {
                var target = edge.Block;
                for (var i = 0; i < target.Parameters.Count; i++)
                    caller.ReplaceAllUsesOfRegister(target.Parameters[i], ((VirtualReg)edge.Args[i]).Id);

                block.Instructions.RemoveAt(block.Instructions.Count - 1);
                foreach (var instruction in target.Instructions)
                {
                    instruction.Parent = block;
                    block.Instructions.Add(instruction);
                }
                target.Instructions.Clear();
                target.Parent = null;
                caller.Blocks.Remove(target);
                caller.RebuildCfg();
            }
        }
    }

    private static bool SelfLoops(MirBlock block) =>
        block.Instructions.Any(i => i.Operands.Any(o => o is BlockTarget t && t.Block == block));
}
