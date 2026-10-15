// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
for (int i = -2; i <= 8; i++)
{
    int r;
    switch (i)
    {
        case 0: r = 10; break;
        case 1: r = 11; break;
        case 2: r = 12; break;
        case 3: r = 13; break;
        case 4: r = 14; break;
        default: r = -1; break;
    }
    Console.WriteLine(r);
}
Console.WriteLine(Pick(300));
Console.WriteLine(Pick(65536));
Console.WriteLine(Pick(unchecked((int)0x80000001)));

static int Pick(int x)
{
    switch (x)
    {
        case 0: return 1;
        case 1: return 2;
        case 2: return 3;
        case 3: return 4;
        default: return 99;
    }
}
