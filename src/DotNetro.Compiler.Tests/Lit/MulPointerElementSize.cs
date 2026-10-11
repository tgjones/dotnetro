// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
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
