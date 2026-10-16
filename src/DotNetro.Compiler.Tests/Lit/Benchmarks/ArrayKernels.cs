// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: array element access (sum, fill, copy, reverse, max).
var a = new int[8];
var b = new int[8];
Bench.Fill(a);
Bench.Copy(a, b);
Bench.Reverse(b);
Console.WriteLine(Bench.Sum(a));
Console.WriteLine(b[0]);
Console.WriteLine(Bench.Max(b));

static class Bench
{
    public static void Fill(int[] a)
    {
        for (int i = 0; i < a.Length; i++) a[i] = i * 3 + 1;
    }

    public static int Sum(int[] a)
    {
        int s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i];
        return s;
    }

    public static void Copy(int[] src, int[] dst)
    {
        for (int i = 0; i < src.Length; i++) dst[i] = src[i];
    }

    public static void Reverse(int[] a)
    {
        for (int i = 0, j = a.Length - 1; i < j; i++, j--)
        {
            int t = a[i]; a[i] = a[j]; a[j] = t;
        }
    }

    public static int Max(int[] a)
    {
        int m = a[0];
        for (int i = 1; i < a.Length; i++) if (a[i] > m) m = a[i];
        return m;
    }
}
