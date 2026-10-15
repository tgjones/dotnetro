// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var p = new Point { X = 3, Y = 4 };
object o = p;
p.X = 100;
var q = (Point)o;
Console.WriteLine(q.X);
Console.WriteLine(q.Y);
Console.WriteLine(p.X);
Console.WriteLine(o is Point);
Console.WriteLine(o is int);

struct Point
{
    public int X;
    public int Y;
}
