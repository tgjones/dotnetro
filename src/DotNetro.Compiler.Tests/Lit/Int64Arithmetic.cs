// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Carries and borrows ripple through every byte; results wrap at 64 bits.
long a = 0x00000000FFFFFFFF;
long b = 1;
PrintLong(a + b);
PrintLong(a - b);
PrintLong(b - a);
long c = 0x00FFFFFFFFFFFFFF;
PrintLong(c + b);
PrintLong(0x7FFFFFFFFFFFFFFF + b);
PrintLong(long.MinValue - b);
PrintLong(-a);
long min = long.MinValue;
PrintLong(-min);
PrintLong(a + a + a);
ulong u = ulong.MaxValue;
PrintULong(u + 2UL);
ulong zero = 0;
PrintULong(zero - 1UL);
PrintLong(123456789012345678L + 876543210987654321L);

// Console.WriteLine(long) is not supported yet: print the two 32-bit halves.
static void PrintLong(long v)
{
    Console.WriteLine((int)(v >> 32));
    Console.WriteLine((int)v);
}

static void PrintULong(ulong v)
{
    Console.WriteLine((int)(v >> 32));
    Console.WriteLine((int)v);
}
