// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = 100000;
var b = -1234;
var c = 0;
Console.WriteLine(-a);
Console.WriteLine(-b);
Console.WriteLine(-c);
Console.WriteLine(-(a - 40000));
