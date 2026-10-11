// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    short value = -2;
    short* p = &value;
    Console.WriteLine(*p);
    *p = 300;
    Console.WriteLine(value);
    ushort other = 65000;
    ushort* q = &other;
    Console.WriteLine(*q);
    *q = (ushort)(*q + 1);
    Console.WriteLine(other);
}
