// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int x = 0x12F4A6C8;
byte lo = (byte)x;
sbyte slo = (sbyte)x;
Console.WriteLine(lo);
Console.WriteLine(slo);
Console.WriteLine((byte)x == 0xC8);
int z = (int)(uint)x;
Console.WriteLine(z);
