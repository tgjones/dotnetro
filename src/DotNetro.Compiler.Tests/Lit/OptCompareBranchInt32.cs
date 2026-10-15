// RUN-dotnet: @cs_compiler --optimize @file | @dotnet_runner
// RUN-emulated: @cs_compiler --optimize @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler --optimize @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static void Check(int a, int b)
{
    if (a == b) Console.WriteLine(1);
    if (a != b) Console.WriteLine(2);
    if (a < b) Console.WriteLine(3);
    if (a <= b) Console.WriteLine(4);
    if (a > b) Console.WriteLine(5);
    if (a >= b) Console.WriteLine(6);
}
Check(-5, 3);
Check(3, -5);
Check(7, 7);
