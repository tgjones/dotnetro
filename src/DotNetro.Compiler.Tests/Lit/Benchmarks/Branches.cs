// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: compare-and-branch heavy i32 code (clamp, sign, abs, collatz steps, switch).
Console.WriteLine(Bench.Clamp(150, 0, 100));
Console.WriteLine(Bench.Clamp(-5, 0, 100));
Console.WriteLine(Bench.Collatz(27));
Console.WriteLine(Bench.Classify(7));
Console.WriteLine(Bench.Classify(-3));

static class Bench
{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    public static int Collatz(int start)
    {
        int n = start;
        int steps = 0;
        while (n != 1) { n = (n & 1) == 0 ? n >> 1 : 3 * n + 1; steps++; }
        return steps;
    }

    public static int Classify(int x)
    {
        if (x < 0) return -1;
        if (x == 0) return 0;
        switch (x)
        {
            case 1: return 10;
            case 2: return 20;
            case 3: return 30;
            default: return 99;
        }
    }
}
