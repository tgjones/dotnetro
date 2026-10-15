// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new Box { Value = 1 };
var b = new Box { Value = 2 };
ref Box r = ref a;
Console.WriteLine(r.Value);
r = b;
Console.WriteLine(a.Value);
class Box { public int Value; }
