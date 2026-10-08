// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = 0x12F4A6C8;
var b = 0x0FF0F0FF;
Console.WriteLine(a & b);
