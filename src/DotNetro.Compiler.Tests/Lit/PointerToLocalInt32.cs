// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    int x = 1234;
    int* p = &x;
    Console.WriteLine(*p);
    *p = 99;
    Console.WriteLine(x);
    *p = *p + 100000;
    Console.WriteLine(x);
}
