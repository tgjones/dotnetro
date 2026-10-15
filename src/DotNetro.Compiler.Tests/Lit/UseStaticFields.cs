// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
MyStructWithStaticFields.A = 1;
MyStructWithStaticFields.B = 2;
Console.WriteLine(MyStructWithStaticFields.A + MyStructWithStaticFields.B);

struct MyStructWithStaticFields
{
    public static int A;
    public static int B;
}
