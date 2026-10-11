// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = new int[4];
Console.WriteLine(a.Length);
Console.WriteLine(a[2]);
a[0] = 100000;
a[1] = -7;
a[3] = a[0] + a[1];
Console.WriteLine(a[0]);
Console.WriteLine(a[1]);
Console.WriteLine(a[3]);
