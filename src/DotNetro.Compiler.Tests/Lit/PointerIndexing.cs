// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    Pair pair = default;
    int* p = (int*)&pair;
    p[0] = 11;
    p[1] = 22;
    Console.WriteLine(pair.A);
    Console.WriteLine(pair.B);
    Console.WriteLine(*(p + 1));
    int i = 1;
    Console.WriteLine(p[i]);
}

struct Pair { public int A; public int B; }
