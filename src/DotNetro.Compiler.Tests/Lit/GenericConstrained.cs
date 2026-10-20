// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Describe(new Named()));
var tag = new Tag { Value = 5 };
Console.WriteLine(DescribeRef(ref tag));
Console.WriteLine(HashRef(ref tag));
var number = 12;
Console.WriteLine(DescribeRef(ref number));
Console.WriteLine(HashRef(ref number));
Console.WriteLine(Describe<object>(new Named()));

static string Describe<T>(T value) => value.ToString();

static int Hash<T>(T value) => value.GetHashCode();

static string DescribeRef<T>(ref T value) => value.ToString();

static int HashRef<T>(ref T value) => value.GetHashCode();

class Named
{
    public override string ToString() => "named";
}

struct Tag
{
    public int Value;

    public override string ToString() => "tag";

    public override int GetHashCode() => Value + 1000;
}
