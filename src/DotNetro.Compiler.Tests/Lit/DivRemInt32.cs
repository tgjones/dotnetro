// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
static void Check(int a, int b)
{
    Console.WriteLine(a / b);
    Console.WriteLine(a % b);
}
Check(100, 7);
Check(-100, 7);
Check(100, -7);
Check(-100, -7);
Check(0, 5);
Check(5, 100);
Check(1000000, 1000);
Check(2147483647, 2);
Check(-2147483647, 65536);
Check(123456789, 12345);
