using System.Text;
using Aemula.Chips.Mos6502;

// A flat-RAM Commodore 64 with the three KERNAL calls the runtime uses stubbed
// out: CHROUT ($FFD2), CHRIN ($FFCF) and GETIN ($FFE4). Text crosses the stubs as
// PETSCII in the lower/upper-case character set (the one the start-up code
// selects) and is mapped to and from ASCII here, so the DIFF lit tests check the
// program's own ASCII<->PETSCII conversion.
//
// The 6510's I/O port at $00/$01 is not emulated. Generated code must never touch
// it (a stray write remaps ROM/RAM/IO on the real machine), so any bus access to
// those addresses fails the run: it would be a code generation bug.
internal sealed class C64TargetSystem : ITargetSystem
{
    private const ushort Chrin = 0xFFCF;
    private const ushort Chrout = 0xFFD2;
    private const ushort Getin = 0xFFE4;

    public string Name => "c64";
    public ushort LoadAddress => 0x080D;

    public void InitialiseMemory(byte[] memory)
    {
        memory[Chrin] = 0x60; // RTS
        memory[Chrout] = 0x60;
        memory[Getin] = 0x60;

        memory[0xFFFC] = 0x00; // Reset vector → $FF00
        memory[0xFFFD] = 0xFF;

        memory[0xFF00] = 0x20; // JSR $080D (the program halts in a `JMP *` rather than returning)
        memory[0xFF01] = 0x0D;
        memory[0xFF02] = 0x08;
    }

    public EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks, CycleMeter? meter = null)
    {
        var cpu = new Mos6502(Mos6502Options.Default);
        ref var pins = ref cpu.Pins;

        var output = new StringBuilder();
        var pending = new Queue<byte>(); // PETSCII bytes of the line being read
        var ticks = 0;
        var running = false; // the reset sequence itself touches low addresses

        while (true)
        {
            cpu.Tick();

            var address = pins.Address;
            if (running && address < 2)
            {
                return new EmulationResult(output.ToString(), EmulationStatus.Fault,
                    $"Program accessed the 6510 I/O port (${address:X4}) at PC=${cpu.PC:X4}; it must stay out of $00/$01.");
            }

            if (pins.RW)
                pins.Data = memory[address];
            else
                memory[address] = pins.Data;

            if (pins.Sync)
            {
                meter?.Observe(pins.Address, cpu.SP, ticks);
                running = true;

                if (HaltDetection.IsHalt(memory, pins.Address))
                    return new EmulationResult(output.ToString(), EmulationStatus.Completed);

                trace?.WriteLine($"{cpu.PC:X4}  A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.P.AsByte(false):X2} SP:{cpu.SP:X2}");
            }

            switch (cpu.PC)
            {
                case Chrout:
                    if (PetsciiToAscii(cpu.A) is { } c)
                        output.Append(c);
                    break;

                case Chrin:
                    // Like the screen editor: read a whole line, then hand it back a character
                    // at a time, ending in CR.
                    if (pending.Count == 0)
                    {
                        var line = input.ReadLine() ?? string.Empty;
                        foreach (var ch in line)
                            pending.Enqueue(AsciiToPetscii(ch));
                        pending.Enqueue(0x0D);
                    }
                    cpu.A = pending.Dequeue();
                    break;

                case Getin:
                    cpu.A = pending.Count > 0 ? pending.Dequeue() : (byte)0; // 0 = no key
                    break;
            }

            ticks++;
            if (ticks >= maxTicks)
                return new EmulationResult(output.ToString(), EmulationStatus.RunawayTimeout);
        }
    }

    // Screen codes in the lower/upper-case set: $41-$5A are lower-case letters and $C1-$DA upper-case.
    // Control codes the start-up code sends ($0E charset select, $8E, $93 clear) print nothing; anything
    // else unmapped shows as '?' so a stray byte is visible in a DIFF.
    private static char? PetsciiToAscii(byte b) => b switch
    {
        0x0D => '\n',
        0x0E or 0x8E or 0x93 => null,
        >= 0x20 and <= 0x40 => (char)b,
        >= 0x41 and <= 0x5A => (char)(b + 0x20),
        0x5B or 0x5D => (char)b,
        >= 0xC1 and <= 0xDA => (char)(b - 0x80),
        _ => '?',
    };

    private static byte AsciiToPetscii(char c) => c switch
    {
        >= 'a' and <= 'z' => (byte)(c - 0x20),
        >= 'A' and <= 'Z' => (byte)(c + 0x80),
        _ => (byte)c,
    };
}
