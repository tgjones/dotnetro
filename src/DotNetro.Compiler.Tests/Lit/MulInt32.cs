// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static int Mul(int a, int b) => a * b;
Console.WriteLine(Mul(6, 7));
Console.WriteLine(Mul(-6, 7));
Console.WriteLine(Mul(-6, -7));
Console.WriteLine(Mul(0, 12345));
Console.WriteLine(Mul(12345, 1));
Console.WriteLine(Mul(100000, 3));
Console.WriteLine(Mul(46341, 46341));
Console.WriteLine(Mul(-2147483647, 2));
