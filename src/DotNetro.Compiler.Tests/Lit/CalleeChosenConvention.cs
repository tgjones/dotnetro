// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// Each function is called from one place and its address is never taken, so its
// placement may choose which of A and X its arguments arrive in and its result leaves
// in. The calls follow, and the values must still come out right.
Console.WriteLine(Equal(-1, 65535));
Console.WriteLine(Difference(10, 3));
Console.WriteLine(Both((byte)200, (byte)5));

static int Equal(short a, ushort b) { if (a == b) return 1; return 0; }

static short Difference(short a, short b) { return (short)(a - b); }

static int Both(byte a, byte b)
{
    if (a < b) return b - a;
    return a - b + 100;
}
