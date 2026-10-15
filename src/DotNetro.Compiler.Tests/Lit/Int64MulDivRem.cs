// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
long a = 1234567890123;
long b = 987654;
PrintLong(a * b);
PrintLong(a / b);
PrintLong(a % b);
PrintLong(-a / b);
PrintLong(-a % b);
PrintLong(a / -b);
PrintLong(a % -b);
PrintLong(a * 8);
PrintLong(a / 16);
PrintLong(a * 1000);
PrintLong(a / 1000);
ulong u = 0xF000000000000001;
PrintULong(u / 3);
PrintULong(u % 1000);
PrintULong(u / 256);
PrintLong(long.MinValue / 7);
PrintLong(long.MaxValue % 1000003);

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
