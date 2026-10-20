// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
unsafe
{
    Console.WriteLine(Size<int>());
    Console.WriteLine(Size<byte>());
    Console.WriteLine(Size<short>());
    Console.WriteLine(Size<Pair>());
    Console.WriteLine(Size<Wrapper<Pair>>());
}

static unsafe int Size<T>() where T : unmanaged => sizeof(T);

struct Pair
{
    public int A;
    public int B;
}

struct Wrapper<T> where T : unmanaged
{
    public T Inner;
    public int Tag;
}
