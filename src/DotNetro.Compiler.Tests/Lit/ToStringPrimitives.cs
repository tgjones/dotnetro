// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
using System;

int i = -12345;
short s = short.MinValue;
byte b = byte.MaxValue;
char c = 'Q';
bool t = true;
Console.WriteLine(i.ToString());
Console.WriteLine(int.MinValue.ToString());
Console.WriteLine(int.MaxValue.ToString());
Console.WriteLine(0.ToString());
Console.WriteLine(s.ToString());
Console.WriteLine(short.MaxValue.ToString());
Console.WriteLine(b.ToString());
Console.WriteLine(((byte)0).ToString());
Console.WriteLine(c.ToString());
Console.WriteLine(t.ToString());
Console.WriteLine(false.ToString());
