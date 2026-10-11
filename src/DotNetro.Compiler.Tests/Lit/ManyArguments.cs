// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
static int Sum(byte a, byte b, byte c, byte d, byte e)
{
    return a + b + b - c + d - e;
}
Console.WriteLine(Sum(1, 2, 3, 4, 5));
Console.WriteLine(Sum(100, 20, 50, 40, 30));
