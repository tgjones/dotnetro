// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    Console.WriteLine(sizeof(Pair));
    Console.WriteLine(sizeof(int));
}
struct Pair { public int A; public int B; }
