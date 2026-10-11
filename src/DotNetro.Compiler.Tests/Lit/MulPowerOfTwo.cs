// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
static int Times8(int x) => x * 8;
static int Times4(int x) => 4 * x;
static int Same(int x) => x * 1;
static int Big(int x) => x * 65536;
Console.WriteLine(Times8(5));
Console.WriteLine(Times8(-3));
Console.WriteLine(Times4(1000));
Console.WriteLine(Same(77));
Console.WriteLine(Big(3));
