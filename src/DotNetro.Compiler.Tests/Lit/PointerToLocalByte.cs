// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    byte b = 7;
    byte* q = &b;
    *q = 200;
    Console.WriteLine(*q);
    Console.WriteLine(b);
    sbyte s = -3;
    sbyte* r = &s;
    Console.WriteLine(*r);
    *r = -100;
    Console.WriteLine(s);
    *q = (byte)(*q + 100);
    Console.WriteLine(b);
}
