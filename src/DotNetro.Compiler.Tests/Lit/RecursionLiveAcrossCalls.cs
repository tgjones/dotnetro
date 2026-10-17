// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// Values that live in zero-page homes across calls that re-enter the caller:
// self recursion, mutual recursion, and recursion through a virtual call. Each
// activation must find its own values intact after the call returns.
Console.WriteLine(Fib(10));
Console.WriteLine(Mix(6, 100, 7));
Console.WriteLine(IsEven(9) ? 1 : 0);
Console.WriteLine(IsEven(10) ? 1 : 0);
Console.WriteLine(Depth(new Branch(new Branch(new Leaf(), new Leaf()), new Leaf())));

static int Fib(int n)
{
    if (n < 2) return n;
    var a = Fib(n - 1);
    var b = Fib(n - 2);
    return a + b;
}

// Keeps several locals alive across two recursive calls.
static int Mix(int n, int x, int y)
{
    if (n == 0) return x + y;
    var left = Mix(n - 1, x + 1, y);
    var right = Mix(n - 1, x, y + 2);
    return left + right + n * x - y;
}

static bool IsEven(int n) => n == 0 || IsOdd(n - 1);
static bool IsOdd(int n) => n != 0 && IsEven(n - 1);

static int Depth(Node node) => node.Depth(1);

abstract class Node
{
    public abstract int Depth(int level);
}

class Leaf : Node
{
    public override int Depth(int level) => level;
}

class Branch(Node left, Node right) : Node
{
    public override int Depth(int level)
    {
        var l = left.Depth(level + 1);
        var r = right.Depth(level + 1);
        return (l > r ? l : r) + 0;
    }
}
