// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
unsafe
{
    Triple t = default;
    Triple* p = &t;
    int i = 0;
    Console.WriteLine((int)((nuint)(p + i) - (nuint)p));
    i = 1;
    Console.WriteLine((int)((nuint)(p + i) - (nuint)p));
    i = 5;
    Console.WriteLine((int)((nuint)(p + i) - (nuint)p));
}

struct Triple { public byte A; public byte B; public byte C; }
