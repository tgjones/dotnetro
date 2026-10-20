// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var p = new Pair<int, byte>(1000, 7);
Console.WriteLine(p.First);
Console.WriteLine(p.Second);
Console.WriteLine(p.Sum());
p.Second = 9;
Console.WriteLine(p.Sum());

var q = new Pair<byte, byte>(3, 4);
Console.WriteLine(q.Sum());

struct Pair<A, B>
{
    public A First;
    public B Second;

    public Pair(A first, B second)
    {
        First = first;
        Second = second;
    }

    public int Sum() => Convert(First) + Convert(Second);

    private static int Convert<T>(T value) => value is int i ? i : value is byte b ? b : 0;
}
