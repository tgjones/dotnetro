// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    delegate*<int, int> f = &Targets.Double;
    Console.WriteLine(f(21));

    delegate*<int, int, int> g = &Targets.Add;
    Console.WriteLine(g(40, 2));

    delegate*<void> h = &Targets.Hello;
    h();

    // Reassigning the pointer dispatches to a different target.
    f = &Targets.Negate;
    Console.WriteLine(f(5));
}

static class Targets
{
    public static int Double(int x) => x * 2;
    public static int Add(int a, int b) => a + b;
    public static int Negate(int x) => -x;
    public static void Hello() => Console.WriteLine("hello");
}
