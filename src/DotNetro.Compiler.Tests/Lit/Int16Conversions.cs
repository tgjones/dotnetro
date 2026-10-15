// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int big = 70000;
Console.WriteLine((short)big);
Console.WriteLine((ushort)big);
int neg = -2;
Console.WriteLine((ushort)neg);
Console.WriteLine((short)neg);
short s = -5;
Console.WriteLine((int)s);
Console.WriteLine((int)(ushort)s);
byte b = 200;
Console.WriteLine((short)b);
sbyte sb = -3;
Console.WriteLine((short)sb);
Console.WriteLine((ushort)sb);
char c = (char)65;
Console.WriteLine((int)c);
Console.WriteLine(c + 1);
