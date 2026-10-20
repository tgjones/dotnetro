// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var p = new Point { X = 1, Y = 2 };
Console.WriteLine(Sum(p));

// The callee gets its own copy.
Console.WriteLine(Bump(p));
Console.WriteLine(p.X);

var q = Make(10, 20);
Console.WriteLine(q.X);
Console.WriteLine(q.Y);
Console.WriteLine(Sum(Make(3, 4)));
Console.WriteLine(Sum(Add(p, q)));

var line = new Line(p, q);
Console.WriteLine(line.From.X);
Console.WriteLine(line.To.Y);
Console.WriteLine(line.Length());
Console.WriteLine(line.Swapped().From.X);

Shape shape = new Square(new Point { X = 5, Y = 6 });
Console.WriteLine(shape.Corner().Y);
Console.WriteLine(shape.Shift(new Point { X = 100, Y = 200 }).X);

Console.WriteLine(Pick(true, p, q).X);
Console.WriteLine(Pick(false, p, q).X);

var box = new Holder<Point>();
box.Value = Make(7, 8);
Console.WriteLine(box.Get().Y);
Console.WriteLine(Sum(box.Get()));

static int Sum(Point point) => point.X + point.Y;

static int Bump(Point point)
{
    point.X += 100;
    return point.X;
}

static Point Make(int x, int y)
{
    var result = new Point { X = x, Y = y };
    return result;
}

static Point Add(Point a, Point b) => new Point { X = a.X + b.X, Y = a.Y + b.Y };

static T Pick<T>(bool first, T a, T b) => first ? a : b;

struct Point
{
    public int X;
    public int Y;
}

struct Line
{
    public Point From;
    public Point To;

    public Line(Point from, Point to)
    {
        From = from;
        To = to;
    }

    public int Length() => To.X - From.X + To.Y - From.Y;

    public Line Swapped() => new Line(To, From);
}

abstract class Shape
{
    public abstract Point Corner();

    public virtual Point Shift(Point by) => new Point { X = by.X + 1, Y = by.Y + 1 };
}

class Square : Shape
{
    private Point _corner;

    public Square(Point corner)
    {
        _corner = corner;
    }

    public override Point Corner() => _corner;
}

class Holder<T>
{
    public T Value;

    public T Get() => Value;
}
