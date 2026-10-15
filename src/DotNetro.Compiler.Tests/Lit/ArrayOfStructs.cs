// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new Pair[3];
a[1].A = 5;
a[1].B = 6;
a[2].B = 9;
Console.WriteLine(a[0].A + a[0].B);
Console.WriteLine(a[1].A + a[1].B);
Console.WriteLine(a[2].B);

struct Pair { public int A; public int B; }
