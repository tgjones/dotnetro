// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    nint v = 5;
    nint* p = &v;
    *p = 4321;
    Console.WriteLine((int)*p);
    Console.WriteLine((int)v);
}
