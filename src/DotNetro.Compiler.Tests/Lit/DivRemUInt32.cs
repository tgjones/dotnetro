// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static void Check(uint a, uint b)
{
    Console.WriteLine((int)(a / b));
    Console.WriteLine((int)(a % b));
}
Check(100, 7);
Check(4000000000, 3);
Check(4000000000, 3000000000);
Check(3000000000, 4000000000);
Check(4294967295, 1);
Check(4294967295, 4294967295);
Check(7, 8);
Check(1000000, 16);
Check(1000001, 16);
