// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = 0x12345679;
Console.WriteLine(a << 1);
Console.WriteLine(a << 3);
Console.WriteLine(a << 8);
Console.WriteLine(a << 13);
Console.WriteLine(a << 24);
Console.WriteLine(a << 31);
Console.WriteLine(a << 0);
