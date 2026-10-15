// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
object o = Color.Blue;
Console.WriteLine((int)(Color)o);
Console.WriteLine(o is Color);
Console.WriteLine(o is int);
Color back = (Color)o;
Console.WriteLine(back == Color.Blue);

enum Color { Red, Green, Blue }
