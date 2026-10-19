using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

/// <summary>
/// Home slots: homes that are never live at the same time share one byte.
/// </summary>
/// <remarks>
/// Placement gives every home class a home of its own, since the class is the unit it
/// reasons about (a class is the set of values that must share a home). A home is only
/// live from a store to its last reload, though, usually a small part of the function,
/// so most homes could share a byte with another. This colours the homes of one
/// function on the final code, after placement and the late passes: two homes interfere
/// when one is written while the other is live (liveness of the home registers
/// themselves, not of the values placement had in them), and the colours become the
/// function's homes `$h0…`. It is per byte (a home is one byte), and runs before the
/// layout assigns addresses, so the layout sees the smaller set.
/// </remarks>
internal static class MOS6502HomeColouring
{
    public static void Colour(MirFunction function)
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
        if (homes.Count == 0) return;

        function.RebuildCfg();
        var (liveIn, liveOut) = MOS6502PhysicalLiveness.Compute(function);
        var interference = homes.ToDictionary(h => h, _ => new HashSet<int>());

        void Interfere(int a, int b)
        {
            if (a == b) return;
            interference[a].Add(b);
            interference[b].Add(a);
        }

        foreach (var block in function.Blocks)
        {
            var live = new HashSet<int>(liveOut[block]);
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                var defined = instr.Operands
                    .OfType<PhysicalReg>()
                    .Where(p => p.IsDefinition && MOS6502Registers.IsHome(p.Id))
                    .Select(p => p.Id)
                    .ToList();
                foreach (var d in defined)
                {
                    foreach (var other in defined)
                        Interfere(d, other);
                    foreach (var l in live)
                        if (MOS6502Registers.IsHome(l))
                            Interfere(d, l);
                }
                MOS6502PhysicalLiveness.Step(instr, live);
            }
        }

        // Homes live into the function are all live together at its start, with no
        // definition to show it.
        var entry = liveIn[function.Blocks[0]].Where(MOS6502Registers.IsHome).ToList();
        foreach (var a in entry)
            foreach (var b in entry)
                Interfere(a, b);

        // Colour the most accessed homes first, so that they get the lowest colours.
        // (DSATUR order saves 0.1% more bytes on the C# programs, not worth its cost.)
        var weights = Weights(function);
        var colourOf = new Dictionary<int, int>();
        foreach (var home in homes.OrderByDescending(h => weights.GetValueOrDefault(h)).ThenBy(h => h))
        {
            var taken = interference[home]
                .Where(colourOf.ContainsKey)
                .Select(n => colourOf[n])
                .ToHashSet();
            var colour = 0;
            while (taken.Contains(colour)) colour++;
            colourOf[home] = colour;
        }

        int Map(int reg) => MOS6502Registers.Home(colourOf[reg]);

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.LiveIns.Count; i++)
                if (MOS6502Registers.IsHome(block.LiveIns[i]))
                    block.LiveIns[i] = Map(block.LiveIns[i]);
            var seen = new HashSet<int>();
            block.LiveIns.RemoveAll(r => MOS6502Registers.IsHome(r) && !seen.Add(r));

            foreach (var instr in block.Instructions)
                for (var i = 0; i < instr.Operands.Length; i++)
                    if (instr.Operands[i] is PhysicalReg p && MOS6502Registers.IsHome(p.Id))
                        instr.Operands[i] = p with { Id = Map(p.Id) };
        }
    }

    /// <summary>How often each home is accessed, weighted by the loop depth of the access.</summary>
    internal static Dictionary<int, long> Weights(MirFunction function)
    {
        var loops = new LoopAnalysis().Compute(function);
        var weights = new Dictionary<int, long>();
        foreach (var block in function.Blocks)
        {
            var frequency = loops.Frequency(block);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg p && MOS6502Registers.IsHome(p.Id))
                        weights[p.Id] = weights.GetValueOrDefault(p.Id) + frequency;
        }
        return weights;
    }
}
