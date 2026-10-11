// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
static uint UDiv16(uint x) => x / 16;
static uint URem8(uint x) => x % 8;
static int SDiv2(int x) => x / 2;
static int SRem4(int x) => x % 4;
Console.WriteLine((int)UDiv16(1000001));
Console.WriteLine((int)URem8(1000001));
Console.WriteLine((int)UDiv16(4000000000));
Console.WriteLine(SDiv2(7));
Console.WriteLine(SDiv2(-7));
Console.WriteLine(SRem4(-7));
Console.WriteLine(SRem4(7));
