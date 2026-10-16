// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Ints(0);
Ints(1);
Ints(3);
Ints(8);
Ints(13);
Ints(24);
Ints(31);
Shorts(0);
Shorts(1);
Shorts(7);
Shorts(8);
Shorts(15);
Longs(0);
Longs(1);
Longs(12);
Longs(32);
Longs(40);
Longs(63);

static void Ints(int count)
{
    int a = 0x12345679;
    int n = -0x12345679;
    uint u = 0xFEDCBA98;
    Console.WriteLine(a << count);
    Console.WriteLine(a >> count);
    Console.WriteLine(n >> count);
    Console.WriteLine((int)(u >> count));
}

static void Shorts(int count)
{
    short s = -12345;
    Console.WriteLine((short)(s << count));
    Console.WriteLine((short)(s >> count));
}

static void Longs(int count)
{
    long l = 0x0123456789ABCDEF;
    PrintLong(l << count);
    PrintLong(l >> count);
}

// Console.WriteLine(long) is not supported yet: print the two 32-bit halves.
static void PrintLong(long v)
{
    Console.WriteLine((int)(v >> 32));
    Console.WriteLine((int)v);
}
