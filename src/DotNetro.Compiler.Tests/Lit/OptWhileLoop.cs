// RUN-dotnet: @cs_compiler --optimize @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler --optimize @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static void Count(int n)
{
    var i = 0;
    while (i < n)
    {
        Console.WriteLine(i);
        i++;
    }
}
static void CountDown(int n)
{
    var i = n;
    while (i != 0)
    {
        Console.WriteLine(i);
        i--;
    }
}
static void Flag(bool b)
{
    if (b) Console.WriteLine(1);
    if (!b) Console.WriteLine(2);
}
Count(4);
CountDown(3);
Flag(true);
Flag(false);
