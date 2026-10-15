// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new byte[3];
a[0] = 200;
a[1] = 7;
a[2] = (byte)(a[0] + a[1]);
Console.WriteLine(a[0]);
Console.WriteLine(a[2]);
var s = new sbyte[2];
s[1] = -5;
Console.WriteLine(s[1]);
Console.WriteLine(s[0]);
