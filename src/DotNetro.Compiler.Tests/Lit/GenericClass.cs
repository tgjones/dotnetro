// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new Box<int>(7);
var b = new Box<byte>(200);
var c = new Box<string>("text");
Console.WriteLine(a.Get());
Console.WriteLine(b.Get());
Console.WriteLine(c.Get());
a.Set(a.Get() + 1);
Console.WriteLine(a.Value);
Console.WriteLine(Box<int>.Describe());

class Box<T>
{
    public T Value;

    public Box(T value)
    {
        Value = value;
    }

    public T Get() => Value;

    public void Set(T value)
    {
        Value = value;
    }

    public static int Describe() => 42;
}
