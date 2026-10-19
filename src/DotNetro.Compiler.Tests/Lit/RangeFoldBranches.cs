// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// A byte promoted to int is always below 256, so the compare is decided and the dead arm is never emitted.
static int AlwaysBelow(byte a)
{
    int x = a;
    if (x < 256)
        return 1;
    return 2;
}
static int NeverEqual(byte a)
{
    int x = a;
    if (x == 1000)
        return 1;
    return 2;
}
static int NeverNegative(byte a)
{
    int x = a;
    if (x >= 0)
        return 1;
    return 2;
}
// The counter stays below 10 inside the loop, so the second test is decided.
static int CountedLoop()
{
    int total = 0;
    for (int i = 0; i < 10; i++)
    {
        if (i < 16)
            total += i;
        else
            total += 100;
    }
    return total;
}
// An undecided compare still branches both ways.
static int Undecided(byte a)
{
    int x = a;
    if (x < 100)
        return 1;
    return 2;
}
Console.WriteLine(AlwaysBelow(0));
Console.WriteLine(AlwaysBelow(255));
Console.WriteLine(NeverEqual(7));
Console.WriteLine(NeverNegative(200));
Console.WriteLine(CountedLoop());
Console.WriteLine(Undecided(99));
Console.WriteLine(Undecided(100));
