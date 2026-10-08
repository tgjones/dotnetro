// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
int a = -5;
int b = 3;
uint x = 4026531840u;
uint y = 3u;
Console.WriteLine(a == b);
Console.WriteLine(a > b);
Console.WriteLine(a != b);
Console.WriteLine(x > y);
Console.WriteLine(x < y);
Console.WriteLine((a < b) == (x < y));
