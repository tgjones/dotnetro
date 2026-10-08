// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
sbyte s = -5;
int x = s + 100;
Console.WriteLine(x);
Console.WriteLine(s);
Console.WriteLine(s < 0);
int y = s;
Console.WriteLine(y + 1 < 0);
