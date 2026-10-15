// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static int Sum(byte a, byte b, byte c, byte d, byte e)
{
    return a + b + b - c + d - e;
}
static byte Wrap(byte a, byte b)
{
    return (byte)(a + b);
}
static int Diff(byte a, byte b)
{
    return a - b;
}
static int Negate(byte a)
{
    return -a;
}
static int Complement(byte a)
{
    return ~a;
}
static int SignedSum(sbyte a, sbyte b)
{
    return a + b;
}
Console.WriteLine(Sum(100, 200, 50, 40, 30));
Console.WriteLine(Wrap(200, 100));
Console.WriteLine(Wrap(1, 2));
Console.WriteLine(Diff(3, 250));
Console.WriteLine(Negate(7));
Console.WriteLine(Complement(5));
Console.WriteLine(SignedSum(-100, -100));
Console.WriteLine(SignedSum(100, 100));
