// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
int a = 1234;
int m = 5;
uint b = 40000;
nint n = a;
nint k = m;
nuint u = (nuint)b;
Console.WriteLine((int)n);
Console.WriteLine((int)k);
Console.WriteLine((int)u);
Console.WriteLine((int)(uint)u);
sbyte s = -3;
Console.WriteLine((int)(uint)s);
Console.WriteLine((int)(byte)a);
