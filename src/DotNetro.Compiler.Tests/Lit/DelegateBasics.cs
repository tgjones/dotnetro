// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// A static method group, an instance method group and a virtual one.
Action hello = Targets.Hello;
hello();

Func<int, int> twice = Targets.Double;
Console.WriteLine(twice(21));

Func<int, int, int> add = Targets.Add;
Console.WriteLine(add(40, 2));

var counter = new Counter();
Action tick = counter.Tick;
tick();
tick();
Console.WriteLine(counter.Count);

Counter derived = new DoubleCounter();
Action dtick = derived.Tick;
dtick();
Console.WriteLine(derived.Count);

// Reassigning dispatches to a different target.
twice = Targets.Negate;
Console.WriteLine(twice(5));

// A delegate passed to a method.
Console.WriteLine(Targets.Apply(Targets.Double, 4));
Console.WriteLine(Targets.Apply(counter.Plus, 4));

static class Targets
{
    public static void Hello() => Console.WriteLine("hello");
    public static int Double(int x) => x * 2;
    public static int Negate(int x) => -x;
    public static int Add(int a, int b) => a + b;
    public static int Apply(Func<int, int> f, int x) => f(x);
}

class Counter
{
    public int Count;
    public virtual void Tick() => Count += 1;
    public int Plus(int x) => Count + x;
}

class DoubleCounter : Counter
{
    public override void Tick() => Count += 10;
}
