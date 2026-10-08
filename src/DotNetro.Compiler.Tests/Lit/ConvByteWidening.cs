// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
byte b = 200;
int x = b + 100;
Console.WriteLine(x);
Console.WriteLine(b);
Console.WriteLine(b > 100);
Console.WriteLine(b & 0x0F);
