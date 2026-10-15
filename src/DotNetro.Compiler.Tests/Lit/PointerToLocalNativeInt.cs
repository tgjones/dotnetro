// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
unsafe
{
    nint v = 5;
    nint* p = &v;
    *p = 4321;
    Console.WriteLine((int)*p);
    Console.WriteLine((int)v);
}
