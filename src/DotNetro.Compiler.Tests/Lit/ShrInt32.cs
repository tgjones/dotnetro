// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = -123456789;
var b = 0x12345679;
Console.WriteLine(a >> 1);
Console.WriteLine(a >> 5);
Console.WriteLine(a >> 8);
Console.WriteLine(a >> 10);
Console.WriteLine(a >> 24);
Console.WriteLine(a >> 31);
Console.WriteLine(b >> 1);
Console.WriteLine(b >> 13);
Console.WriteLine(b >> 31);
