// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int x = 3;
Console.WriteLine(100 + (x > 2 ? 1 : 2));
Console.WriteLine(Add(5, x > 2 ? 10 : 20, x < 2 ? 100 : 200));

static int Add(int a, int b, int c) => a + b + c;
