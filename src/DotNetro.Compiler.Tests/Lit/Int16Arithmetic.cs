// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
short s = 32767;
Console.WriteLine(s + 1);
Console.WriteLine(s * 2);
short n = -300;
Console.WriteLine(n);
Console.WriteLine(n - 1);
Console.WriteLine(n / 7);
ushort u = 65535;
Console.WriteLine(u);
Console.WriteLine(u + 1);
ushort w = 40000;
Console.WriteLine(w + w);
s++;
Console.WriteLine(s);
u++;
Console.WriteLine(u);
Console.WriteLine(n < 0 ? 1 : 0);
Console.WriteLine(w > 30000 ? 1 : 0);
