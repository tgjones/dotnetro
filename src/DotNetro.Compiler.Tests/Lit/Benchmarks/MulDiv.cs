// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: i32 multiply / divide / remainder (runtime helper calls).
Console.WriteLine(Bench.Factorial(10));
Console.WriteLine(Bench.DigitSum(987654321));
Console.WriteLine(Bench.Gcd(1071, 462));

static class Bench
{
    public static int Factorial(int n)
    {
        int r = 1;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }

    public static int DigitSum(int value)
    {
        int n = value;
        int s = 0;
        while (n != 0) { s += n % 10; n /= 10; }
        return s;
    }

    public static int Gcd(int x, int y)
    {
        int a = x, b = y;
        while (b != 0) { int t = a % b; a = b; b = t; }
        return a;
    }
}
