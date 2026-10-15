// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
unsafe
{
    // Pick the target at run time and pass it around as a parameter.
    for (var i = 0; i < 3; i++)
    {
        delegate*<byte, byte> op = i == 0 ? &Targets.Inc : i == 1 ? &Targets.Dec : &Targets.Twice;
        Console.WriteLine(Targets.Apply(op, 10));
    }

    Console.WriteLine(Targets.Sum(&Targets.Square, 4));
}

static unsafe class Targets
{
    public static byte Inc(byte x) => (byte)(x + 1);
    public static byte Dec(byte x) => (byte)(x - 1);
    public static byte Twice(byte x) => (byte)(x * 2);
    public static int Square(int x) => x * x;

    public static byte Apply(delegate*<byte, byte> op, byte value) => op(value);

    // Sum of f(1)..f(n).
    public static int Sum(delegate*<int, int> f, int n)
    {
        var total = 0;
        for (var i = 1; i <= n; i++) total += f(i);
        return total;
    }
}
