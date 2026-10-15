// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
PrintLong(Sum(1, 2));
PrintLong(Sum(0x100000000, 0x200000000));
PrintLong(Mix(1, 2L, 3));
PrintLong(Fib(60));
PrintLong(Factorial(20));

static long Sum(long a, long b) => a + b;
static long Mix(int a, long b, short c) => a + b * c;

static long Fib(int n)
{
    long a = 0, b = 1;
    for (int i = 0; i < n; i++)
    {
        long t = a + b;
        a = b;
        b = t;
    }
    return a;
}

static long Factorial(int n)
{
    long r = 1;
    for (int i = 2; i <= n; i++)
        r *= i;
    return r;
}

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
