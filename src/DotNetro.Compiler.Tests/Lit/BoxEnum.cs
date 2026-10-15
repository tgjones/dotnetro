// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
object o = Color.Blue;
Console.WriteLine((int)(Color)o);
Console.WriteLine(o is Color);
Console.WriteLine(o is int);
Color back = (Color)o;
Console.WriteLine(back == Color.Blue);

enum Color { Red, Green, Blue }
