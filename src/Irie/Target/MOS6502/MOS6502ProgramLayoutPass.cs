using Irie.MachineCode;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

/// <summary>
/// Whole-program zero-page layout. Each function's zero-page footprint is one
/// contiguous BLOCK: its homes ($h0, $h1, …, the allocator's function-local pool)
/// followed by, where the target allows it, the frame slots promoted to zero page.
/// The pass places every block in the target's zero-page window with the static
/// stack colouring of StaticFrameColorer — a function's block sits above the
/// blocks of all its callees, so a call never overwrites anything its caller keeps
/// there, and nothing is saved or restored around a call. Independent subtrees of
/// the call graph reuse the same bytes, so the budget used is the deepest call path,
/// not the sum over all functions.
/// </summary>
/// <remarks>
/// Then it rewrites each home to the `RC(n)` of its final address, so every later
/// pass and the emitter see ordinary zero-page registers. It runs post-RA (the
/// homes are final), after MOS6502RecursionSavePass, and before the generic
/// FrameAccessLoweringPass so the placement it stamps on frame slots is honoured
/// when the addressing mode is chosen.
///
/// Frame slots (spills, locals) of a NON-reentrant function are promoted into its
/// block when `promoteFrameSlots` is set: a promoted slot's symbol resolves to its
/// zero-page address everywhere (MirGlobal.FixedAddress), so the encoder reserves no
/// .bss byte for it. Slots of reentrant functions stay in absolute memory.
///
/// The target's library routines (libcalls.s) are leaf nodes of the call graph that
/// the graph itself does not see. A routine declares the bytes of zero page it
/// scratches above the ABI registers (`.zeropage N`), which start at the window's
/// first byte (the code relies on them being contiguous with RC19), so layout
/// treats them as a block at offset 0 of the window: every function that reaches
/// the routine, directly or through the library's own jumps, is placed above it.
/// The routine then saves nothing around its use of those bytes.
///
/// A CallerStackArg slot is never promoted: it isn't this function's own storage,
/// it reads the caller's dynamically positioned stack-argument space.
///
/// With `allowAbsoluteHomes` (the placement keeps a home for every value of
/// the function) a home that does not fit in zero page lives in a byte of absolute
/// memory instead: every instruction that names a home has an absolute twin
/// (`lda.zp` -&gt; `lda.abs` …), so demoting a home never invalidates the code, it
/// only makes each access a byte and a cycle dearer. Zero page goes to the homes
/// accessed most often, weighted by the loop depth of the access.
/// </remarks>
public sealed class MOS6502ProgramLayoutPass(
    int zeroPageBase, int windowStart, int windowEnd, bool promoteFrameSlots,
    MachineCodeModule? library = null, bool allowAbsoluteHomes = false) : Pass
{
    public override string Name => "MOS6502ProgramLayout";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        ReentrancyAnalysis.Run(module);

        // Homes that are never live together share one byte.
        var homesPlaced = module.Functions.Sum(f => UsedHomes(f).Count);
        foreach (var function in module.Functions)
            MOS6502HomeColouring.Colour(function);

        // The homes each function uses, and how many of them keep a zero-page byte.
        var usedHomes = new Dictionary<MirFunction, List<int>>();
        var homeCount = new Dictionary<MirFunction, int>();
        foreach (var function in module.Functions)
        {
            usedHomes[function] = UsedHomes(function);
            homeCount[function] = allowAbsoluteHomes ? usedHomes[function].Count : CountHomes(function);
        }
        var zeroPageHomes = new Dictionary<MirFunction, int>(homeCount);

        var graph = CallGraph.Build(module);
        var libraryFloor = LibraryFloors(module);

        // Zero page can run out. Frame slots are optional there (their absolute
        // twin is always correct, just slower), so a function that does not fit
        // gives its slots back to absolute memory and the layout is redone;
        // homes have no such fallback, so a function that still overflows is an
        // error.
        var demoted = new HashSet<MirFunction>();
        StaticFrameColorer.Result result;
        while (true)
        {
            result = new StaticFrameColorer(
                graph,
                f => zeroPageHomes[f] + (demoted.Contains(f) ? 0 : SlotBytes(f)),
                budgetSize: windowEnd - windowStart,
                floor: f => libraryFloor.GetValueOrDefault(f)).Colour();
            if (result.Overflow.Count == 0) break;
            // Give back the biggest remaining set of slots (a callee's block can
            // push a caller over, so the overflowing function need not own them).
            var victim = module.Functions
                .Where(f => !demoted.Contains(f) && SlotBytes(f) > 0)
                .OrderByDescending(SlotBytes).FirstOrDefault();
            if (victim is not null)
            {
                demoted.Add(victim);
                continue;
            }
            if (!allowAbsoluteHomes
                || !DemoteHomes(graph, result, zeroPageHomes, windowEnd - windowStart,
                    f => zeroPageHomes[f] + (demoted.Contains(f) ? 0 : SlotBytes(f))))
                break;
        }

        if (result.Overflow.Count > 0)
            throw new NotSupportedException(
                $"MOS6502ProgramLayout: zero page exhausted: {result.Overflow[0].Name} needs " +
                $"{zeroPageHomes[result.Overflow[0]]} bytes of homes above its callees at " +
                $"${windowStart + result.Base[result.Overflow[0]]:X2}, past ${windowEnd:X2}.");

        foreach (var (function, floor) in libraryFloor)
            if (floor > windowEnd - windowStart)
                throw new NotSupportedException(
                    $"MOS6502ProgramLayout: zero page exhausted: a library routine reached from " +
                    $"{function.Name} needs {floor} bytes, past ${windowEnd:X2}.");

        context.Statistics["homes.placed"] = homesPlaced;
        context.Statistics["homes.slots"] = module.Functions.Sum(f => usedHomes[f].Count);
        context.Statistics["homes.zero-page-bytes"] = module.Functions.Sum(f => zeroPageHomes[f]);
        context.Statistics["zero-page.deepest-path"] = module.Functions
            .Select(f => result.Base.TryGetValue(f, out var b) ? b + zeroPageHomes[f] + (demoted.Contains(f) ? 0 : SlotBytes(f)) : 0)
            .DefaultIfEmpty(0).Max();

        var globalsByName = new Dictionary<string, MirGlobal>();
        foreach (var global in module.Globals)
            globalsByName[global.SymbolName] = global;

        foreach (var function in module.Functions)
        {
            var baseOf = result.Base.GetValueOrDefault(function);
            var homes = zeroPageHomes[function];
            var blockStart = windowStart + baseOf;

            if (allowAbsoluteHomes)
                PlaceHomes(module, function, usedHomes[function], homes, blockStart, globalsByName);
            else if (homes > 0)
                RewriteHomes(function, address => MOS6502Registers.RC(address - zeroPageBase), blockStart);

            var within = homes;
            if (!promoteFrameSlots || !function.IsNonReentrant || demoted.Contains(function)) continue;
            foreach (var slot in function.FrameSlots)
            {
                if (slot.StackId != FrameSlot.DefaultStackId) continue;
                slot.StackId = MOS6502FrameStackIds.ZeroPage;
                slot.Offset = blockStart + within;
                if (globalsByName.TryGetValue(slot.SymbolName, out var global))
                    global.FixedAddress = slot.Offset;
                within += slot.Type.SizeInBits / 8;
            }
        }
    }

    /// <summary>
    /// For each function of the module, the zero-page bytes scratched by the
    /// library routines it calls (the largest footprint among them).
    /// </summary>
    private Dictionary<MirFunction, int> LibraryFloors(MirModule module)
    {
        var floors = new Dictionary<MirFunction, int>();
        if (library is null) return floors;

        var routines = library.Functions.ToDictionary(f => f.Name);
        var footprints = new Dictionary<string, int>();
        foreach (var function in module.Functions)
        {
            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions)
                    foreach (var op in instr.Operands)
                        if (op is Symbol s && routines.ContainsKey(s.Name))
                        {
                            if (!footprints.TryGetValue(s.Name, out var bytes))
                                footprints[s.Name] = bytes = FootprintOf(s.Name, routines);
                            if (bytes > 0)
                                floors[function] = Math.Max(floors.GetValueOrDefault(function), bytes);
                        }
        }
        return floors;
    }

    /// <summary>The largest footprint among a library routine and every routine it reaches.</summary>
    private static int FootprintOf(string name, Dictionary<string, MachineCodeFunction> routines)
    {
        var seen = new HashSet<string> { name };
        var pending = new Stack<string>([name]);
        var bytes = 0;
        while (pending.Count > 0)
        {
            var routine = routines[pending.Pop()];
            bytes = Math.Max(bytes, routine.ZeroPageFootprint);
            foreach (var entry in routine.Body)
                if (entry is MachineCodeInstruction instruction)
                    foreach (var op in instruction.Operands)
                        if (op is MachineCodeOperand.ExternalRef r
                            && routines.ContainsKey(r.Name) && seen.Add(r.Name))
                            pending.Push(r.Name);
        }
        return bytes;
    }

    /// <summary>Bytes of the frame slots this function will place in zero page.</summary>
    private int SlotBytes(MirFunction function)
    {
        if (!promoteFrameSlots || !function.IsNonReentrant) return 0;
        var size = 0;
        foreach (var slot in function.FrameSlots)
            if (slot.StackId == FrameSlot.DefaultStackId)
                size += slot.Type.SizeInBits / 8;
        return size;
    }

    /// <summary>One more than the highest home index the function mentions.</summary>
    private static int CountHomes(MirFunction function)
    {
        var count = 0;
        foreach (var block in function.Blocks)
        {
            foreach (var reg in block.LiveIns)
                if (MOS6502Registers.IsHome(reg))
                    count = Math.Max(count, reg - MOS6502Registers.HomeBase + 1);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg p && MOS6502Registers.IsHome(p.Id))
                        count = Math.Max(count, p.Id - MOS6502Registers.HomeBase + 1);
        }
        return count;
    }

    /// <summary>The homes a function mentions, ascending.</summary>
    private static List<int> UsedHomes(MirFunction function)
    {
        var homes = new SortedSet<int>();
        foreach (var block in function.Blocks)
        {
            foreach (var reg in block.LiveIns)
                if (MOS6502Registers.IsHome(reg)) homes.Add(reg);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg p && MOS6502Registers.IsHome(p.Id))
                        homes.Add(p.Id);
        }
        return [.. homes];
    }

    /// <summary>
    /// Zero page ran out: take zero-page homes away from the function that overflows
    /// or, since a callee's block sits under its caller's, from the callee with the
    /// most of them, by as many as the overflow needs. False if there are none left.
    /// </summary>
    private static bool DemoteHomes(
        CallGraph graph, StaticFrameColorer.Result result, Dictionary<MirFunction, int> zeroPageHomes,
        int budget, Func<MirFunction, int> size)
    {
        var overflowing = result.Overflow[0];
        var excess = result.Base[overflowing] + size(overflowing) - budget;

        var seen = new HashSet<int> { graph.IndexByName[overflowing.Name] };
        var pending = new Stack<int>(seen);
        MirFunction? victim = null;
        while (pending.Count > 0)
        {
            var index = pending.Pop();
            var function = graph.Functions[index];
            if (zeroPageHomes[function] > (victim is null ? 0 : zeroPageHomes[victim]))
                victim = function;
            foreach (var callee in graph.Callees[index])
                if (seen.Add(callee))
                    pending.Push(callee);
        }

        if (victim is null) return false;
        zeroPageHomes[victim] -= Math.Min(zeroPageHomes[victim], Math.Max(excess, 1));
        return true;
    }

    /// <summary>
    /// Gives each home of the function its place — the first `zeroPageCount` by
    /// access weight a zero-page byte at the function's block, the rest a byte of
    /// absolute memory — and rewrites the code to match.
    /// </summary>
    private void PlaceHomes(
        MirModule module, MirFunction function, List<int> used, int zeroPageCount, int blockStart,
        Dictionary<string, MirGlobal> globalsByName)
    {
        if (used.Count == 0) return;

        var weights = MOS6502HomeColouring.Weights(function);
        var inZeroPage = used
            .OrderByDescending(h => weights.GetValueOrDefault(h))
            .ThenBy(h => h)
            .Take(zeroPageCount)
            .Order()
            .ToList();

        var zeroPage = new Dictionary<int, int>();
        for (var k = 0; k < inZeroPage.Count; k++)
            zeroPage[inZeroPage[k]] = MOS6502Registers.RC(blockStart + k - zeroPageBase);

        var absolute = new Dictionary<int, Symbol>();
        foreach (var home in used.Where(h => !zeroPage.ContainsKey(h)))
        {
            var name = $"{function.Name}_h{home - MOS6502Registers.HomeBase}";
            if (!globalsByName.ContainsKey(name))
            {
                var global = new MirGlobal(name, IRType.I8, 1, null);
                module.Globals.Add(global);
                globalsByName[name] = global;
            }
            absolute[home] = new Symbol(name);
        }

        foreach (var block in function.Blocks)
        {
            for (var i = block.LiveIns.Count - 1; i >= 0; i--)
            {
                var reg = block.LiveIns[i];
                if (zeroPage.TryGetValue(reg, out var zp)) block.LiveIns[i] = zp;
                else if (absolute.ContainsKey(reg)) block.LiveIns.RemoveAt(i);
            }

            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (!instr.Operands.Any(o => o is PhysicalReg p && MOS6502Registers.IsHome(p.Id))) continue;

                var operands = (MirOperand[])instr.Operands.Clone();
                var inMemory = -1;
                for (var k = 0; k < operands.Length; k++)
                {
                    if (operands[k] is not PhysicalReg p || !MOS6502Registers.IsHome(p.Id)) continue;
                    if (zeroPage.TryGetValue(p.Id, out var zp))
                        operands[k] = p with { Id = zp };
                    else if (!p.IsImplicit && inMemory < 0)
                        inMemory = k;
                    else
                        throw new NotSupportedException(
                            $"MOS6502ProgramLayout: a home of {function.Name} in absolute memory is used by an " +
                            $"instruction ({DescribeOp(instr)}) that has no absolute form for it.");
                }

                if (inMemory < 0)
                {
                    instr.Operands = operands;
                    continue;
                }

                var symbol = absolute[((PhysicalReg)operands[inMemory]).Id];
                var (op, rewritten) = AbsoluteTwin(instr, operands, inMemory, symbol, function);
                block.Instructions[i] = new MirInstruction(MOS6502Dialect.OpRef(op), rewritten) { Parent = block };
            }
        }
    }

    private static string DescribeOp(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        return $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";
    }

    /// <summary>
    /// The absolute-memory form of an instruction whose operand `at` is a home that
    /// lives in absolute memory at `symbol`.
    /// </summary>
    private static (MOS6502Op, MirOperand[]) AbsoluteTwin(
        MirInstruction instr, MirOperand[] operands, int at, Symbol symbol, MirFunction function)
    {
        if (instr.Opcode.Dialect == MOS6502Dialect.Id)
        {
            var twin = (MOS6502Op)instr.Opcode.Code switch
            {
                MOS6502Op.LdaZp => MOS6502Op.LdaAbs,
                MOS6502Op.LdxZp => MOS6502Op.LdxAbs,
                MOS6502Op.LdyZp => MOS6502Op.LdyAbs,
                MOS6502Op.AdcZp => MOS6502Op.AdcAbs,
                MOS6502Op.SbcZp => MOS6502Op.SbcAbs,
                MOS6502Op.AndZp => MOS6502Op.AndAbs,
                MOS6502Op.OraZp => MOS6502Op.OraAbs,
                MOS6502Op.EorZp => MOS6502Op.EorAbs,
                MOS6502Op.CmpZp => MOS6502Op.CmpAbs,
                MOS6502Op.CpxZp => MOS6502Op.CpxAbs,
                MOS6502Op.CpyZp => MOS6502Op.CpyAbs,
                _ => (MOS6502Op?)null,
            };
            if (twin is { } absolute)
            {
                operands[at] = symbol;
                return (absolute, operands);
            }

            // `$home = sta.zp $reg` is `sta.abs $reg, @home`: the value, then the address.
            var store = (MOS6502Op)instr.Opcode.Code switch
            {
                MOS6502Op.StaZp => MOS6502Op.StaAbs,
                MOS6502Op.StxZp => MOS6502Op.StxAbs,
                MOS6502Op.StyZp => MOS6502Op.StyAbs,
                _ => (MOS6502Op?)null,
            };
            if (store is { } storeTwin && at == 0 && operands.Length == 2)
                return (storeTwin, [operands[1], symbol]);
        }

        throw new NotSupportedException(
            $"MOS6502ProgramLayout: a home of {function.Name} lives in absolute memory but {DescribeOp(instr)} " +
            "has no absolute twin.");
    }

    private static void RewriteHomes(MirFunction function, Func<int, int> registerOfAddress, int blockStart)
    {
        int Map(int reg) => registerOfAddress(blockStart + reg - MOS6502Registers.HomeBase);

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.LiveIns.Count; i++)
                if (MOS6502Registers.IsHome(block.LiveIns[i]))
                    block.LiveIns[i] = Map(block.LiveIns[i]);
            foreach (var instr in block.Instructions)
                for (var i = 0; i < instr.Operands.Length; i++)
                    if (instr.Operands[i] is PhysicalReg p && MOS6502Registers.IsHome(p.Id))
                        instr.Operands[i] = p with { Id = Map(p.Id) };
        }
    }
}
