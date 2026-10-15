// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
object a = new Plain();
object b = new Plain();
Console.WriteLine(a.Equals(a));
Console.WriteLine(a.Equals(b));
Console.WriteLine(a.Equals(null));
Console.WriteLine(object.ReferenceEquals(a, b));

object c = new Custom(7);
Console.WriteLine(c.Equals(new Custom(7)));
Console.WriteLine(c.Equals(new Custom(8)));
Console.WriteLine(c.GetHashCode());
Console.WriteLine(c.ToString());

Base d = new Derived(3);
Console.WriteLine(d.ToString());
Console.WriteLine(d.GetHashCode());

class Plain
{
}

class Custom(int value)
{
    public int Value = value;

    public override bool Equals(object obj) => obj is Custom other && other.Value == Value;

    public override int GetHashCode() => Value;

    public override string ToString() => "Custom";
}

class Base(int value)
{
    protected int Value = value;

    public override string ToString() => "Base";

    public override int GetHashCode() => Value;
}

class Derived(int value) : Base(value)
{
    public override string ToString() => "Derived";

    public override int GetHashCode() => Value * 2;
}
