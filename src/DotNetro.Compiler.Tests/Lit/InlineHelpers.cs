// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// Small helpers the compiler inlines (each has one call site): a value-returning one, one with a side
// effect on a static, one with a branch and two returns, one with a loop, one with an address-taken local,
// and a recursive one it must leave alone.
Console.WriteLine(Twice(21));
for (var i = -1; i <= 3; i++)
    Console.WriteLine(Clamp(i * 70 - 10));
Counter.Bump();
Counter.Bump();
Console.WriteLine(Counter.Value);
Console.WriteLine(Sum(10));
Console.WriteLine(ViaLocal(8));
Console.WriteLine(Fact(5));

static int Twice(int x) => x + x;

static int Clamp(int x)
{
    if (x < 0) return 0;
    if (x > 100) return 100;
    return x;
}

static int Sum(int n)
{
    var total = 0;
    while (n != 0)
    {
        total += n;
        n--;
    }
    return total;
}

static unsafe int ViaLocal(int x)
{
    int local = x;
    int* p = &local;
    *p = *p * 2 + 1;
    return local;
}

static int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);

static class Counter
{
    public static int Value;
    public static void Bump() { Value = Value + 1; }
}
