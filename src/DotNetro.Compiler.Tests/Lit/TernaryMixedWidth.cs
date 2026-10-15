// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
byte[] data = new byte[2];
data[1] = 9;
int i = 1;
Console.WriteLine(i > 0 ? data[1] : 300);
Console.WriteLine(i > 5 ? data[1] : 300);
