// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int x = 3;
Console.WriteLine(x > 2 ? 10 : 20);
Console.WriteLine(x > 5 ? 10 : 20);
Console.WriteLine(x == 3 ? x + 1 : x - 1);
Console.WriteLine(Pick(true, 7, 9));
Console.WriteLine(Pick(false, 7, 9));
Console.WriteLine(Sign(-4));
Console.WriteLine(Sign(0));
Console.WriteLine(Sign(12));
int y = x < 2 ? 1 : 2;
Console.WriteLine(y);

static int Pick(bool flag, int a, int b) => flag ? a : b;
static int Sign(int v) => v < 0 ? -1 : v == 0 ? 0 : 1;
