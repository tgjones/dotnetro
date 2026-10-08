// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
int x = 0x12F4A6C8;
byte lo = (byte)x;
sbyte slo = (sbyte)x;
Console.WriteLine(lo);
Console.WriteLine(slo);
Console.WriteLine((byte)x == 0xC8);
int z = (int)(uint)x;
Console.WriteLine(z);
