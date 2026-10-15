internal interface ITargetSystem
{
    string Name { get; }
    ushort LoadAddress { get; }
    void InitialiseMemory(byte[] memory);
    EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks);
}

internal sealed record EmulationResult(string Output, EmulationStatus Status);

internal enum EmulationStatus { Completed, RunawayTimeout }

internal static class HaltDetection
{
    // The program ends in `JMP *` (the compiler's entry function halts when Main returns).
    // `address` is where an opcode is being fetched.
    public static bool IsHalt(byte[] memory, ushort address) =>
        memory[address] == 0x4C
        && memory[(ushort)(address + 1)] == (byte)address
        && memory[(ushort)(address + 2)] == (byte)(address >> 8);
}
