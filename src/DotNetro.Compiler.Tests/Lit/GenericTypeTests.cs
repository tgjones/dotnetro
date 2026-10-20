// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
object box = new Box<int>(1);
Console.WriteLine(box is Box<int>);
Console.WriteLine(box is Box<byte>);
Console.WriteLine(((Box<int>)box).Value);

object seven = 7;
Console.WriteLine(Is<int>(seven));
Console.WriteLine(Is<byte>(seven));
Console.WriteLine(Is<string>(seven));
Console.WriteLine(Is<string>("s"));
Console.WriteLine(Is<Box<int>>(box));
Console.WriteLine(Is<Box<byte>>(box));
Console.WriteLine(Cast<int>(seven));
Console.WriteLine(Cast<Box<int>>(box).Value);
Console.WriteLine(Unbox(Wrap(200)));
Console.WriteLine(Unbox(Wrap((byte)201)));
Console.WriteLine(Wrap("str") is string);
Console.WriteLine(Wrap(5) is int);

static bool Is<T>(object o) => o is T;

static T Cast<T>(object o) => (T)o;

static object Wrap<T>(T value) => value;

static int Unbox(object o) => o is int i ? i : o is byte b ? b : -1;

class Box<T>
{
    public T Value;

    public Box(T value)
    {
        Value = value;
    }
}
