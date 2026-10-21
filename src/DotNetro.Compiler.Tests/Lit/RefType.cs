// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// __reftype is refanytype; typeof is ldtoken of a type. Types compare by their handles.
int number = 5;
TypedReference reference = __makeref(number);
Console.WriteLine(__reftype(reference) == typeof(int));
Console.WriteLine(__reftype(reference) == typeof(byte));
Console.WriteLine(__reftype(reference) != typeof(Point));

var point = new Point();
Console.WriteLine(Describe(__makeref(point)));
Console.WriteLine(Describe(__makeref(number)));

var box = new Box();
Console.WriteLine(Describe(__makeref(box)));

Console.WriteLine(typeof(Box) == typeof(Box));
Console.WriteLine(typeof(Box) == typeof(Point));
Console.WriteLine(typeof(Box).Equals(typeof(Box)));

static string Describe(TypedReference reference)
{
    var type = __reftype(reference);
    if (type == typeof(Point))
        return "Point";
    if (type == typeof(Box))
        return "Box";
    if (type == typeof(int))
        return "int";
    return "other";
}

struct Point
{
    public int X, Y;
}

class Box
{
}
