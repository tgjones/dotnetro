// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// The zero-page pointer pair used for indirect loads must not be assumed to
// hold the previous block's pointer at a join block.
static int Sum(byte[] p, byte[] q, int flag)
{
    int a = p[0];
    if (flag == 0)
    {
        a += q[1];
    }
    return a + q[0];
}

var p = new byte[2];
var q = new byte[2];
p[0] = 5; p[1] = 7;
q[0] = 20; q[1] = 30;
Console.WriteLine(Sum(p, q, 0));
Console.WriteLine(Sum(p, q, 1));
