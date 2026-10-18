using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Calls that can re-enter the calling function: the one place a home is saved.
//
// Program layout (MOS6502ProgramLayoutPass) stacks every function's homes above
// those of its callees, so a call normally cannot disturb a live home. That
// guarantee ends inside a recursive cycle (self- or mutual recursion, or a
// vtable/delegate call that leads back): the callee may be another activation of
// the same function, with the very same homes. So around each call that stays in
// the caller's strongly connected component, this pass saves the homes that are
// live across the call on the hardware stack, `LDA home ; PHA` before and
// `PLA ; STA home` after, in reverse order — exactly what the callee-saved scheme
// paid on every function, now paid only where recursion makes it necessary.
//
// The accumulator is the only pushable register, so it is borrowed. $y is never
// an argument or result register, so a live $a (an argument going into the call,
// a result coming out) is parked in $y meanwhile, as a pseudo.copy that
// CopyPropagation can clean up.
//
// Runs post-RA, while homes are still symbolic, before the layout pass.
public sealed class MOS6502RecursionSavePass : Pass
{
    public override string Name => "MOS6502RecursionSave";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        var graph = CallGraph.Build(module);

        for (var f = 0; f < module.Functions.Count; f++)
        {
            var scc = graph.SccId[f];
            if (graph.SccSize[scc] == 1 && !graph.SelfRecursive[f])
                continue;
            RunOnFunction(module.Functions[f], f, graph);
        }
    }

    private static void RunOnFunction(MirFunction function, int index, CallGraph graph)
    {
        // Which calls re-enter the function's own component?
        bool ReEnters(string? callee)
        {
            var scc = graph.SccId[index];
            if (callee is not null)
                return graph.IndexByName.TryGetValue(callee, out var c) && graph.SccId[c] == scc;
            for (var j = 0; j < graph.Functions.Count; j++)
                if (graph.AddressTaken[j] && graph.SccId[j] == scc)
                    return true;
            return false;
        }

        function.RebuildCfg();
        var liveOut = MOS6502PhysicalLiveness.Compute(function).LiveOut;

        foreach (var block in function.Blocks)
        {
            // Walk backwards, so the live set at each call is known and inserting
            // around a call does not disturb the indices still to visit.
            var live = new HashSet<int>(liveOut[block]);
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                if (CallGraph.IsCall(instr, out var callee) && ReEnters(callee))
                {
                    var homes = live.Where(MOS6502Registers.IsHome).OrderBy(r => r).ToList();
                    if (homes.Count > 0)
                        SaveAround(block, i, homes, live);
                }
                MOS6502PhysicalLiveness.Step(instr, live);
            }
        }
    }

    // `live` is the set live immediately AFTER the call.
    private static void SaveAround(MirBlock block, int callIndex, List<int> homes, HashSet<int> liveAfter)
    {
        var call = block.Instructions[callIndex];
        const int A = MOS6502Registers.A;
        const int Y = MOS6502Registers.Y;

        var argInA = call.Operands.Any(o => o is PhysicalReg { IsDefinition: false } p && p.Id == A);
        var resultInA = liveAfter.Contains(A);
        if (call.Operands.Any(o => o is PhysicalReg { IsDefinition: false } p && p.Id == Y) || liveAfter.Contains(Y))
            throw new InvalidOperationException(
                $"MOS6502RecursionSave: $y is live across a call in {block.Parent?.Name}; it is borrowed to preserve $a.");

        // After the call (inserted first so the indices before it stay valid).
        var after = new List<(OpcodeRef, MirOperand[])>();
        if (resultInA) after.Add(Copy(Y, A));
        for (var k = homes.Count - 1; k >= 0; k--)
        {
            after.Add((MOS6502Dialect.OpRef(MOS6502Op.Pla), []));
            after.Add((MOS6502Dialect.OpRef(MOS6502Op.StaZp),
                [new PhysicalReg(homes[k], IsDefinition: true), new PhysicalReg(A, IsDefinition: false)]));
        }
        if (resultInA) after.Add(Copy(A, Y));
        var at = callIndex + 1;
        foreach (var (opcode, operands) in after)
            block.InsertInstruction(at++, opcode, operands);

        // Before the call.
        var before = new List<(OpcodeRef, MirOperand[])>();
        if (argInA) before.Add(Copy(Y, A));
        foreach (var home in homes)
        {
            before.Add((MOS6502Dialect.OpRef(MOS6502Op.LdaZp),
                [new PhysicalReg(A, IsDefinition: true), new PhysicalReg(home, IsDefinition: false)]));
            before.Add((MOS6502Dialect.OpRef(MOS6502Op.Pha), []));
        }
        if (argInA) before.Add(Copy(A, Y));
        at = callIndex;
        foreach (var (opcode, operands) in before)
            block.InsertInstruction(at++, opcode, operands);
    }

    private static (OpcodeRef, MirOperand[]) Copy(int dst, int src) =>
        (PseudoDialect.OpRef(PseudoOp.Copy),
         [new PhysicalReg(dst, IsDefinition: true), new PhysicalReg(src, IsDefinition: false)]);
}
