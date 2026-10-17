namespace Irie.MachineCode;

// Links a target's library of hand-written routines (the libcalls the legalizer
// emits calls to: __mulhi3, …) into a program. Only the library functions the
// program actually reaches are added: a function is linked when something
// references it by name and the program does not already define it, and the
// functions linked are scanned in turn, so a routine's own calls come along.
// A definition already in the program wins, so a front end can supply its own
// version of any routine.
public static class MachineCodeLinker
{
    public static void Link(MachineCodeModule program, MachineCodeModule library)
    {
        var defined = new HashSet<string>(program.Functions.Select(f => f.Name));
        var available = library.Functions.ToDictionary(f => f.Name);

        var pending = new Queue<MachineCodeFunction>(program.Functions);
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Dequeue().Body)
            {
                if (entry is not MachineCodeInstruction instruction)
                    continue;
                foreach (var operand in instruction.Operands)
                {
                    if (operand is MachineCodeOperand.ExternalRef reference
                        && !defined.Contains(reference.Name)
                        && available.TryGetValue(reference.Name, out var function))
                    {
                        defined.Add(function.Name);
                        program.Functions.Add(function);
                        pending.Enqueue(function);
                    }
                }
            }
        }
    }
}
