// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// Cases that share a target, cases that jump to the default's block, and no default.
for (int i = 0; i < 8; i++)
{
    int r = 0;
    switch (i)
    {
        case 0:
        case 2:
        case 4: r = 1; break;
        case 1:
        case 3: r = 2; break;
        case 6: r = 3; break;
        case 5:
        case 7: break;
    }
    Console.WriteLine(r);
}
Console.WriteLine(Sparse(0));
Console.WriteLine(Sparse(1));
Console.WriteLine(Sparse(2));
Console.WriteLine(Sparse(3));
Console.WriteLine(Sparse(4));

static int Sparse(int x)
{
    // Holes in the table are routed to the default.
    switch (x)
    {
        case 0: return 5;
        case 1: return 6;
        case 3: return 8;
        case 4: return 9;
        default: return 0;
    }
}
