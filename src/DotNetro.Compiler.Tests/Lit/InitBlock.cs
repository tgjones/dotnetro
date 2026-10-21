// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --max-ticks 20000000
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64 --max-ticks 20000000
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

using System.Runtime.CompilerServices;

// Unsafe.InitBlock is the initblk instruction on the host runtime. Roslyn compiles it as a call,
// so on DotNetro it is the intrinsic method that initblk itself also lowers to.
var small = new byte[10];
for (var i = 0; i < small.Length; i++)
    small[i] = 1;
Fill(small, 2, 0x5A, 5);
for (var i = 0; i < small.Length; i++)
    Console.WriteLine(small[i]);

// A zero-length fill changes nothing.
Fill(small, 0, 9, 0);
Console.WriteLine(small[0]);

// Counts around the 256-byte page size, which the fill routine handles as whole pages plus a tail.
var big = new byte[600];
Check(big, 1);
Check(big, 255);
Check(big, 256);
Check(big, 257);
Check(big, 511);
Check(big, 512);
Check(big, 513);
Check(big, 600);

// Fill `count` bytes with 7 over a buffer of 200s and report whether exactly the first `count`
// bytes changed.
static void Check(byte[] buffer, uint count)
{
    for (var i = 0; i < buffer.Length; i++)
        buffer[i] = 200;
    Fill(buffer, 0, 7, count);

    var correct = true;
    for (var i = 0; i < buffer.Length; i++)
    {
        var expected = i < count ? (byte)7 : (byte)200;
        if (buffer[i] != expected)
            correct = false;
    }
    Console.WriteLine((int)count);
    Console.WriteLine(correct);
}

static unsafe void Fill(byte[] buffer, int index, byte value, uint count)
{
    fixed (byte* p = &buffer[index])
        Unsafe.InitBlock(p, value, count);
}
