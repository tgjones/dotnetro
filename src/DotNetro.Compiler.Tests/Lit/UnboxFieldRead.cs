// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// Reading a field straight out of a cast box compiles to `unbox` + `ldfld`
// (a cast to a copy would be `unbox.any`).
object o = new Point { X = 3, Y = 4 };
Console.WriteLine(GetX(o));
Console.WriteLine(GetY(o));

object p = new Point { X = 30000, Y = -7 };
Console.WriteLine(GetX(p));
Console.WriteLine(GetY(p));

static int GetX(object o) => ((Point)o).X;
static int GetY(object o) => ((Point)o).Y;

struct Point
{
    public int X;
    public int Y;
}
