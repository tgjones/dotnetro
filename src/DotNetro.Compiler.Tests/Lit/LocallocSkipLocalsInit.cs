// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// CHECK-emulated: 99
// CHECK-emulated: 0
// CHECK-emulated: 0
// CHECK-emulated-c64: 99
// CHECK-emulated-c64: 0
// CHECK-emulated-c64: 0

// localloc zeroes its memory, unless the method has [SkipLocalsInit]. What an uninitialised
// allocation holds is unspecified, so there is no host-runtime comparison: this checks that
// the emulated program (whose allocation area is reused LIFO) really skips the zeroing (the zeroed call also wipes the dirt, so the last line is 0).
Dirty();
Console.WriteLine(Skipped());
Console.WriteLine(Zeroed());
Console.WriteLine(Skipped());

static unsafe void Dirty()
{
    byte* p = stackalloc byte[4];
    p[0] = 99;
}

static unsafe int Zeroed()
{
    byte* p = stackalloc byte[4];
    return p[0];
}

[System.Runtime.CompilerServices.SkipLocalsInit]
static unsafe int Skipped()
{
    byte* p = stackalloc byte[4];
    return p[0];
}
