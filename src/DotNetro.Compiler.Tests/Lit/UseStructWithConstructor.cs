// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var s = new MyStructWithConstructor(1, 2);
Console.WriteLine(s.A + s.B);

struct MyStructWithConstructor(int a, int b)
{
    public int A = a;
    public int B = b;
}
