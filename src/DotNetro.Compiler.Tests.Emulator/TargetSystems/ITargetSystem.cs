internal interface ITargetSystem
{
    string Name { get; }
    ushort LoadAddress { get; }
    void InitialiseMemory(byte[] memory);
    EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks, CycleMeter? meter = null);
}

internal sealed record EmulationResult(string Output, EmulationStatus Status, string? Message = null);

internal enum EmulationStatus { Completed, RunawayTimeout, Fault }

internal static class HaltDetection
{
    // The program ends in `JMP *` (the compiler's entry function halts when Main returns).
    // `address` is where an opcode is being fetched.
    public static bool IsHalt(byte[] memory, ushort address) =>
        memory[address] == 0x4C
        && memory[(ushort)(address + 1)] == (byte)address
        && memory[(ushort)(address + 2)] == (byte)(address >> 8);
}

// Counts the cycles spent in one function: from the fetch of its first opcode to
// the first fetch after it returns (the stack pointer is back above where the call
// left it), summed over all calls. Every Tick of the CPU is one clock cycle, so
// the tick count at an opcode fetch is the cycle count so far. A call made while
// the function is already running (recursion) is part of the outer call.
internal sealed class CycleMeter(ushort entryAddress)
{
    private int _startTick;
    private int _entrySp;
    private bool _inside;

    public long Cycles { get; private set; }
    public int Calls { get; private set; }

    // The program stopped in its halt loop: a window that is still open (the program
    // as a whole, measured from its entry) ends here.
    public void Halt(int ticks)
    {
        if (!_inside) return;
        Cycles += ticks - _startTick;
        _inside = false;
    }

    // Called at every opcode fetch: `ticks` cycles have elapsed before it.
    public void Observe(ushort pc, byte sp, int ticks)
    {
        if (_inside)
        {
            if (sp > _entrySp)
            {
                Cycles += ticks - _startTick;
                _inside = false;
            }
        }
        else if (pc == entryAddress)
        {
            _inside = true;
            _entrySp = sp;
            _startTick = ticks;
            Calls++;
        }
    }
}
