// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int x = 1, y = 2;
Swap(ref x, ref y);
Console.WriteLine(x);
Console.WriteLine(y);

byte p = 10, q = 20;
Swap(ref p, ref q);
Console.WriteLine(p);
Console.WriteLine(q);

var s = new Item { Id = 1 };
var t = new Item { Id = 2 };
Swap(ref s, ref t);
Console.WriteLine(s.Id);
Console.WriteLine(t.Id);

static void Swap<T>(ref T a, ref T b)
{
    T tmp = a;
    a = b;
    b = tmp;
}

class Item
{
    public int Id;
}
