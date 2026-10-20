// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var p = new Point { X = 3, Y = 4 };
var q = new Point { X = 30, Y = 40 };
Swap(ref p, ref q);
Console.WriteLine(p.X);
Console.WriteLine(p.Y);
Console.WriteLine(q.X);
Console.WriteLine(q.Y);
Assign(ref p, ref q);
q.X = 99;
Console.WriteLine(p.X);
Console.WriteLine(q.X);

static void Swap<T>(ref T a, ref T b)
{
    T tmp = a;
    a = b;
    b = tmp;
}

static void Assign<T>(ref T dst, ref T src)
{
    dst = src;
}

struct Point
{
    public int X;
    public int Y;
}
