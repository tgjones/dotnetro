// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = new Box { Value = 1 };
var b = new Box { Value = 2 };
ref Box r = ref a;
Console.WriteLine(r.Value);
r = b;
Console.WriteLine(a.Value);
class Box { public int Value; }
