// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static int Sum(byte a, byte b, byte c, byte d, byte e)
{
    return a + b + b - c + d - e;
}
Console.WriteLine(Sum(1, 2, 3, 4, 5));
Console.WriteLine(Sum(100, 200, 50, 40, 30));
