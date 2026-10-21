// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --max-ticks 20000000
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64 --max-ticks 20000000
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// stackalloc is localloc: zeroed memory freed when the method returns.
Console.WriteLine(Sum(10));
Console.WriteLine(Sum(100));

// Nested: the callee's allocation must not disturb the caller's.
Console.WriteLine(Outer(5));

// Freed on return, or 50 calls of 1000 bytes would exhaust memory (the C64 has the most, about 38K).
var total = 0;
for (var i = 0; i < 50; i++)
    total += Touch(1000);
Console.WriteLine(total);

static unsafe int Sum(int n)
{
    byte* p = stackalloc byte[n];
    var sum = p[0] + p[n - 1]; // zero-initialised
    for (var i = 0; i < n; i++)
        p[i] = (byte)(i + 1);
    for (var i = 0; i < n; i++)
        sum += p[i];
    return sum;
}

static unsafe int Touch(int n)
{
    byte* p = stackalloc byte[n];
    p[n - 1] = 7;
    return p[0] + p[n - 1];
}

static unsafe int Outer(int n)
{
    byte* p = stackalloc byte[n];
    for (var i = 0; i < n; i++)
        p[i] = (byte)(i * 3);
    var inner = Sum(7);
    var sum = inner;
    for (var i = 0; i < n; i++)
        sum += p[i];
    return sum;
}
