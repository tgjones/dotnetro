// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Pick(true, 3, 9));
Console.WriteLine(Pick(false, (byte)200, (byte)7));
Console.WriteLine((int)Pick(true, 100000L, 99999L));
Console.WriteLine(Pick(false, (short)-5, (short)-9));

static T Pick<T>(bool first, T a, T b) => first ? a : b;
