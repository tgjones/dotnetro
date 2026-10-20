// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var pair = new Pair<int, byte> { First = 5, Second = 6 };
var box = new Box<Pair<int, byte>>();
box.Value = pair;
Console.WriteLine(box.Value.First);
Console.WriteLine(box.Value.Second);

var inner = new Box<int>();
inner.Value = 77;
var outer = new Box<Box<int>>();
outer.Value = inner;
Console.WriteLine(outer.Value.Value);

class Box<T>
{
    public T Value;
}

struct Pair<A, B>
{
    public A First;
    public B Second;
}
