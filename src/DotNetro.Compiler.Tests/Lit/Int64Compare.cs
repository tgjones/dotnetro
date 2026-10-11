// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Check(0, 0);
Check(1, 2);
Check(-1, 1);
Check(0x100000000, 0xFFFFFFFF);
Check(-0x100000000, -0xFFFFFFFF);
Check(long.MinValue, long.MaxValue);
Check(0x0102030405060708, 0x0102030405060709);
Check(0x0102030405060708, 0x0102030405060608);
Check(0x7100000000000000, 0x7200000000000000);

static void Check(long a, long b)
{
    Console.WriteLine(a == b);
    Console.WriteLine(a != b);
    Console.WriteLine(a < b);
    Console.WriteLine(a <= b);
    Console.WriteLine(a > b);
    Console.WriteLine(a >= b);
    ulong ua = (ulong)a;
    ulong ub = (ulong)b;
    Console.WriteLine(ua < ub);
    Console.WriteLine(ua >= ub);
    if (a < b) Console.WriteLine(1); else Console.WriteLine(2);
    if (ua > ub) Console.WriteLine(3); else Console.WriteLine(4);
    if (a == 0) Console.WriteLine(5);
    if (a != 0) Console.WriteLine(6);
}
