// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
byte b = 200;
int x = b + 100;
Console.WriteLine(x);
Console.WriteLine(b);
Console.WriteLine(b > 100);
Console.WriteLine(b & 0x0F);
