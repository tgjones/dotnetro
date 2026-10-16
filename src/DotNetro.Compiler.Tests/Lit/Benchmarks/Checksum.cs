// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: i32 bitwise ops and shifts (xorshift mixing + rotate checksum).
Console.WriteLine(Bench.Mix(12345, 20));
Console.WriteLine(Bench.Rotate(0x12345678, 5));

static class Bench
{
    public static int Mix(int seed, int rounds)
    {
        int x = seed;
        for (int i = 0; i < rounds; i++)
        {
            x ^= x << 13;
            x ^= (int)((uint)x >> 17);
            x ^= x << 5;
        }
        return x;
    }

    public static int Rotate(int x, int n) => (int)(((uint)x << n) | ((uint)x >> (32 - n)));
}
