// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
byte b = 0xB5;
Console.WriteLine(b << 4);
Console.WriteLine(b >> 3);
sbyte s = -75;
Console.WriteLine(s >> 2);
