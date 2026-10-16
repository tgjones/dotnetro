// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Benchmark: field access through references (class fields, struct fields, virtual call).
var p = new Point { X = 3, Y = 4 };
Bench.Move(p, 10, 20);
Console.WriteLine(Bench.Dist1(p));
Console.WriteLine(Bench.Dot(5, 6));
Shape sh = new Rect { W = 6, H = 7 };
Console.WriteLine(sh.Area());

static class Bench
{
    public static void Move(Point p, int dx, int dy) { p.X += dx; p.Y += dy; }
    public static int Dot(int x, int y)
    {
        var v = new Vec { X = x, Y = y };
        return v.X * v.X + v.Y * v.Y;
    }
    public static int Dist1(Point p) => (p.X < 0 ? -p.X : p.X) + (p.Y < 0 ? -p.Y : p.Y);
}

public class Point { public int X; public int Y; }
public struct Vec { public int X; public int Y; }
public abstract class Shape { public abstract int Area(); }
public class Rect : Shape { public int W; public int H; public override int Area() => W * H; }
