// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    static void Bump(int* p, byte* q)
    {
        *p = *p + 1;
        *q = (byte)(*q + 1);
    }
    int x = 41;
    byte b = 255;
    Bump(&x, &b);
    Console.WriteLine(x);
    Console.WriteLine(b);
}
