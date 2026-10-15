// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = 100000;
var b = -1234;
var c = 0;
Console.WriteLine(-a);
Console.WriteLine(-b);
Console.WriteLine(-c);
Console.WriteLine(-(a - 40000));
