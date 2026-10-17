using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// Post-RA verifier for physreg-only MIR — the llvm `MachineVerifier` slot after
// the virtual-register rewriter. Independent of how the allocator works: it only
// reads the IR the allocator left behind, so it holds for any allocator.
//
// Checks (throws MirVerificationException on the first violation):
//   1. No virtual registers, block parameters or block-argument edges remain.
//   2. Tied operands name the same physreg at both ends.
//   3. A physreg in a class-constrained operand slot belongs to that class.
//   4. No allocatable physreg is read before it is written: every register live
//      on entry to the function must be one of the entry block's declared
//      (calling-convention) live-ins.
//
// The allocator's own interference check (RegisterAssignmentVerifier) covers
// "two simultaneously-live values never share a physreg"; that needs the pre-RA
// intervals, so it cannot be recovered from this pass's input alone.
//
// Runs right after RegisterAllocatorPass. It does NOT demand an absence of
// `pseudo.copy`: copies are expanded later, and scavenger temps minted by that
// expansion legitimately appear as vregs again until RegisterScavengingPass
// (see `finalLowering`).
public sealed class PostRegisterAllocationVerifierPass(
    TargetRegisterInfo registerInfo, bool finalLowering = false) : MirFunctionPass
{
    public override string Name => finalLowering ? "FinalMirVerifier" : "PostRegisterAllocationVerifier";

    public override void Run(MirFunction function)
    {
        foreach (var block in function.Blocks)
        {
            if (block.Parameters.Count > 0)
                Fail(function, $"bb{function.Blocks.IndexOf(block)} still has block parameters");

            foreach (var instr in block.Instructions)
            {
                CheckOperands(function, instr);
                if (finalLowering)
                    CheckFullyLowered(function, instr);
            }
        }

        if (!finalLowering)
            CheckNoReadBeforeWrite(function);
    }

    private void CheckOperands(MirFunction function, MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        var info = dialect.GetInstructionInfo(instr.Opcode.Code);
        var ops = instr.Operands;

        for (var i = 0; i < ops.Length; i++)
        {
            switch (ops[i])
            {
                case VirtualReg v:
                    Fail(function, $"{Describe(instr)} still has virtual register %{v.Id}");
                    break;
                case BlockTarget { Args.Length: > 0 }:
                    Fail(function, $"{Describe(instr)} still carries block arguments");
                    break;
                case PhysicalReg p:
                    if (info.OperandClasses is { } classes && i < classes.Length && classes[i] != 0
                        && !registerInfo.IsRegisterInClass(p.Id, classes[i]))
                        Fail(function, $"{Describe(instr)}: operand {i} is {registerInfo.GetRegisterName(p.Id)}, " +
                                       $"not in required class {registerInfo.GetRegisterClassName(classes[i])}");
                    break;
            }

            var tied = info.GetTiedToIndex(i);
            if (tied >= 0 && tied < ops.Length
                && ops[i] is PhysicalReg a && ops[tied] is PhysicalReg b && a.Id != b.Id)
                Fail(function, $"{Describe(instr)}: tied operands {i} and {tied} are " +
                               $"{registerInfo.GetRegisterName(a.Id)} and {registerInfo.GetRegisterName(b.Id)}");
        }
    }

    // After the last lowering pass nothing abstract may remain: the emitter would
    // otherwise throw (or worse, not) on an unmapped opcode.
    private void CheckFullyLowered(MirFunction function, MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        if (dialect.Prefix is "pseudo" or "core" or "arith" or "cf" or "cast" or "call" or "mem")
            Fail(function, $"{Describe(instr)} survived to the end of the pipeline");
    }

    // Backward may-be-live dataflow over allocatable physregs. A register live on
    // entry that the ABI did not declare as a live-in is read without ever being
    // written on some path.
    private void CheckNoReadBeforeWrite(MirFunction function)
    {
        if (function.Blocks.Count == 0) return;
        function.RebuildCfg();

        var liveIn = function.Blocks.ToDictionary(b => b, _ => new HashSet<int>());
        bool changed;
        do
        {
            changed = false;
            for (var bi = function.Blocks.Count - 1; bi >= 0; bi--)
            {
                var block = function.Blocks[bi];
                var live = new HashSet<int>();
                foreach (var succ in block.Successors)
                    live.UnionWith(liveIn[succ]);

                for (var ii = block.Instructions.Count - 1; ii >= 0; ii--)
                {
                    var instr = block.Instructions[ii];
                    foreach (var op in instr.Operands)
                        if (op is PhysicalReg { IsDefinition: true } d)
                            live.Remove(d.Id);
                    foreach (var op in instr.Operands)
                        if (op is PhysicalReg { IsDefinition: false } u && IsTracked(u.Id))
                            live.Add(u.Id);
                }

                if (!live.SetEquals(liveIn[block]))
                {
                    liveIn[block] = live;
                    changed = true;
                }
            }
        } while (changed);

        var entry = function.Blocks[0];
        foreach (var reg in liveIn[entry].OrderBy(r => r))
            if (!entry.LiveIns.Contains(reg))
                Fail(function, $"{registerInfo.GetRegisterName(reg)} is read before it is written " +
                               "(live on entry but not a declared live-in)");
    }

    // Only registers the allocator hands out are checked; fixed registers (soft
    // stack pointer, S, …) are live-in by convention and have no class.
    private bool IsTracked(int physReg) => registerInfo.ClassOfPhysicalRegister(physReg) != 0;

    private string Describe(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        return $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";
    }

    private static void Fail(MirFunction function, string message) =>
        throw new MirVerificationException($"MirVerifier: in @{function.Name}, {message}");
}
