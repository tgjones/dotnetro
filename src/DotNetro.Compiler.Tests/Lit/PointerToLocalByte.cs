// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
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
