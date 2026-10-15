// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
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
