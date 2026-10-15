// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = 0x89ABCDEF;
Console.WriteLine((int)(a >> 1));
Console.WriteLine((int)(a >> 7));
Console.WriteLine((int)(a >> 8));
Console.WriteLine((int)(a >> 12));
Console.WriteLine((int)(a >> 24));
Console.WriteLine((int)(a >> 31));
