using Irie.MachineCode;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Whole-program zero-page layout. Each function's zero-page footprint is one
// contiguous BLOCK: its homes ($h0, $h1, …, the allocator's function-local pool)
// followed by, where the target allows it, the frame slots promoted to zero page.
// The pass places every block in the target's zero-page window with the static
// stack colouring of StaticFrameColorer — a function's block sits above the
// blocks of all its callees, so a call never overwrites anything its caller keeps
// there, and nothing is saved or restored around a call. Independent subtrees of
// the call graph reuse the same bytes, so the budget used is the deepest call path,
// not the sum over all functions.
//
// Then it rewrites each home to the `RC(n)` of its final address, so every later
// pass and the emitter see ordinary zero-page registers. It runs post-RA (the
// homes are final), after MOS6502RecursionSavePass, and before the generic
// FrameAccessLoweringPass so the placement it stamps on frame slots is honoured
// when the addressing mode is chosen.
//
// Frame slots (spills, locals) of a NON-reentrant function are promoted into its
// block when `promoteFrameSlots` is set: a promoted slot's symbol resolves to its
// zero-page address everywhere (MirGlobal.FixedAddress), so the encoder reserves no
// .bss byte for it. Slots of reentrant functions stay in absolute memory.
//
// The target's library routines (libcalls.s) are leaf nodes of the call graph that
// the graph itself does not see. A routine declares the bytes of zero page it
// scratches above the ABI registers (`.zeropage N`), which start at the window's
// first byte (the code relies on them being contiguous with RC19), so layout
// treats them as a block at offset 0 of the window: every function that reaches
// the routine, directly or through the library's own jumps, is placed above it.
// The routine then saves nothing around its use of those bytes.
//
// A CallerStackArg slot is never promoted: it isn't this function's own storage,
// it reads the caller's dynamically positioned stack-argument space.
public sealed class MOS6502ProgramLayoutPass(
    int zeroPageBase, int windowStart, int windowEnd, bool promoteFrameSlots,
    MachineCodeModule? library = null) : Pass
{
    public override string Name => "MOS6502ProgramLayout";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        ReentrancyAnalysis.Run(module);

        var homeCount = new Dictionary<MirFunction, int>();
        foreach (var function in module.Functions)
            homeCount[function] = CountHomes(function);

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
                f => homeCount[f] + (demoted.Contains(f) ? 0 : SlotBytes(f)),
                budgetSize: windowEnd - windowStart,
                floor: f => libraryFloor.GetValueOrDefault(f)).Colour();
            if (result.Overflow.Count == 0) break;
            // Give back the biggest remaining set of slots (a callee's block can
            // push a caller over, so the overflowing function need not own them).
            var victim = module.Functions
                .Where(f => !demoted.Contains(f) && SlotBytes(f) > 0)
                .OrderByDescending(SlotBytes).FirstOrDefault();
            if (victim is null) break;
            demoted.Add(victim);
        }

        if (result.Overflow.Count > 0)
            throw new NotSupportedException(
                $"MOS6502ProgramLayout: zero page exhausted: {result.Overflow[0].Name} needs " +
                $"{homeCount[result.Overflow[0]]} bytes of homes above its callees at " +
                $"${windowStart + result.Base[result.Overflow[0]]:X2}, past ${windowEnd:X2}.");

        foreach (var (function, floor) in libraryFloor)
            if (floor > windowEnd - windowStart)
                throw new NotSupportedException(
                    $"MOS6502ProgramLayout: zero page exhausted: a library routine reached from " +
                    $"{function.Name} needs {floor} bytes, past ${windowEnd:X2}.");

        var globalsByName = new Dictionary<string, MirGlobal>();
        foreach (var global in module.Globals)
            globalsByName[global.SymbolName] = global;

        foreach (var (function, baseOf) in result.Base)
        {
            var homes = homeCount[function];
            var blockStart = windowStart + baseOf;

            if (homes > 0)
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

    // For each function of the module, the zero-page bytes scratched by the
    // library routines it calls (the largest footprint among them).
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

    // The largest footprint among a library routine and every routine it reaches.
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

    // Bytes of the frame slots this function will place in zero page.
    private int SlotBytes(MirFunction function)
    {
        if (!promoteFrameSlots || !function.IsNonReentrant) return 0;
        var size = 0;
        foreach (var slot in function.FrameSlots)
            if (slot.StackId == FrameSlot.DefaultStackId)
                size += slot.Type.SizeInBits / 8;
        return size;
    }

    // One more than the highest home index the function mentions.
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
