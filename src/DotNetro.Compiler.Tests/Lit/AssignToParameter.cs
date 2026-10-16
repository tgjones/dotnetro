// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Mix(1234));
Console.WriteLine(CountDigits(98765));
Console.WriteLine(Clamp(300, 7));
Console.WriteLine(Clamp(3, 7));

static int Mix(int x)
{
    x ^= x << 13;
    x ^= x >> 7;
    return x;
}

static int CountDigits(int n)
{
    var digits = 0;
    while (n != 0)
    {
        n /= 10;
        digits++;
    }
    return digits;
}

static int Clamp(int value, int max)
{
    if (value > max)
        value = max;
    return value;
}
