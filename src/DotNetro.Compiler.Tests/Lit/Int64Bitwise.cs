// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
long a = 0x0123456789ABCDEF;
long b = unchecked((long)0xF0F0F0F0F0F0F0F0);
PrintLong(a & b);
PrintLong(a | b);
PrintLong(a ^ b);
PrintLong(~a);
PrintLong(~0L);
PrintLong(a & 0xFF);
PrintLong((a >> 8) & 0xFF);
PrintLong(a ^ a);

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
