// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int a = 123456;
int b = 123456;
if (a == b) Console.WriteLine(1); else Console.WriteLine(-1);
if (a != b) Console.WriteLine(2); else Console.WriteLine(-2);
if (a < b) Console.WriteLine(3); else Console.WriteLine(-3);
if (a <= b) Console.WriteLine(4); else Console.WriteLine(-4);
if (a > b) Console.WriteLine(5); else Console.WriteLine(-5);
if (a >= b) Console.WriteLine(6); else Console.WriteLine(-6);
if (b >= a) Console.WriteLine(7); else Console.WriteLine(-7);
