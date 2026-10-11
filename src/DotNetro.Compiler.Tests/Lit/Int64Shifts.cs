// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
long a = 0x0123456789ABCDEF;
long n = -0x0123456789ABCDEF;
ulong u = 0xFEDCBA9876543210;
PrintLong(a << 1);
PrintLong(a << 4);
PrintLong(a << 8);
PrintLong(a << 13);
PrintLong(a << 32);
PrintLong(a << 63);
PrintLong(a >> 1);
PrintLong(a >> 12);
PrintLong(a >> 40);
PrintLong(n >> 1);
PrintLong(n >> 12);
PrintLong(n >> 40);
PrintLong(n >> 63);
PrintULong(u >> 1);
PrintULong(u >> 12);
PrintULong(u >> 40);
PrintULong(u >> 63);

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
