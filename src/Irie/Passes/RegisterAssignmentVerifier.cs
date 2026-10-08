using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// Checks a finished vreg→physreg assignment against the intervals it was
// computed from, independent of the algorithm that produced it:
//   * two vregs whose live intervals overlap never share a physreg;
//   * a vreg never shares a physreg with a physreg-interval that overlaps it
//     (calling-convention copies, clobbers, flag defs);
//   * a physreg in a class-constrained operand slot belongs to that class.
// Called by RegisterAllocatorPass between colouring and rewriting, while the
// vregs still exist to be compared.
public static class RegisterAssignmentVerifier
{
    public static void Verify(
        MirFunction function, TargetRegisterInfo registerInfo,
        LiveIntervals intervals, IReadOnlyDictionary<int, int> assignment)
    {
        foreach (var group in assignment.GroupBy(kv => kv.Value))
        {
            var sharing = group.Select(kv => kv.Key).OrderBy(v => v).ToList();
            var physReg = group.Key;

            for (var i = 0; i < sharing.Count; i++)
            {
                if (intervals.Overlaps(sharing[i], physReg))
                    Fail(function, $"%{sharing[i]} is assigned {registerInfo.GetRegisterName(physReg)}, " +
                                   "which is busy (clobbered or reserved) while it is live");

                for (var j = i + 1; j < sharing.Count; j++)
                    if (intervals.Interfere(sharing[i], sharing[j]))
                        Fail(function, $"%{sharing[i]} and %{sharing[j]} are simultaneously live " +
                                       $"but both assigned {registerInfo.GetRegisterName(physReg)}");
            }
        }

        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
            {
                var classes = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code).OperandClasses;
                if (classes == null) continue;

                for (var i = 0; i < instr.Operands.Length && i < classes.Length; i++)
                    if (classes[i] != 0
                        && instr.Operands[i] is VirtualReg v
                        && assignment.TryGetValue(v.Id, out var phys)
                        && !RegisterAllocationSupport.Contains(registerInfo.GetAllocatableRegisters(classes[i]), phys))
                        Fail(function, $"%{v.Id} is assigned {registerInfo.GetRegisterName(phys)}, " +
                                       $"not in class {registerInfo.GetRegisterClassName(classes[i])} " +
                                       "required by its operand slot");
            }
    }

    private static void Fail(MirFunction function, string message) =>
        throw new MirVerificationException($"RegisterAllocator verifier: in @{function.Name}, {message}");
}
