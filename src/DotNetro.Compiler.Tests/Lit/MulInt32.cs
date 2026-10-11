// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
static int Mul(int a, int b) => a * b;
Console.WriteLine(Mul(6, 7));
Console.WriteLine(Mul(-6, 7));
Console.WriteLine(Mul(-6, -7));
Console.WriteLine(Mul(0, 12345));
Console.WriteLine(Mul(12345, 1));
Console.WriteLine(Mul(100000, 3));
Console.WriteLine(Mul(46341, 46341));
Console.WriteLine(Mul(-2147483647, 2));
