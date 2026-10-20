// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Default<int>());
Console.WriteLine(Default<byte>());
Console.WriteLine(Default<string>() == null);
Console.WriteLine(Default<Item>() == null);
var pair = new Pair { A = 1, B = 2 };
Reset(ref pair);
Console.WriteLine(pair.A);
Console.WriteLine(pair.B);
var number = 9;
Reset(ref number);
Console.WriteLine(number);

var holder = new Holder<int>();
Console.WriteLine(holder.Get());
var refHolder = new Holder<Item>();
Console.WriteLine(refHolder.Get() == null);

static T Default<T>()
{
    T value = default;
    return value;
}

static void Reset<T>(ref T value)
{
    value = default;
}

class Holder<T>
{
    public T Value = default;

    public T Get() => Value;
}

class Item
{
}

struct Pair
{
    public int A;
    public int B;
}
