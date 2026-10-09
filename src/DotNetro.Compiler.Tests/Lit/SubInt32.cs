// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = 100000;
var b = 1234;
Console.WriteLine(a - b);
Console.WriteLine(b - a);
Console.WriteLine(a - a);
