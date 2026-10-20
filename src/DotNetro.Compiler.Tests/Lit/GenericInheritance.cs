// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Base<int> b = new Derived();
Console.WriteLine(b.Get());
Base<int> c = new Base<int>(5);
Console.WriteLine(c.Get());
Base<byte> d = new DerivedByte();
Console.WriteLine(d.Get());
var e = new Derived();
Console.WriteLine(e.Twice(4));

class Base<T>
{
    protected T Item;

    public Base()
    {
    }

    public Base(T item)
    {
        Item = item;
    }

    public virtual T Get() => Item;
}

class Derived : Base<int>
{
    public override int Get() => 100;

    public int Twice(int x) => x * 2;
}

class DerivedByte : Base<byte>
{
    public DerivedByte() : base(9)
    {
    }
}
