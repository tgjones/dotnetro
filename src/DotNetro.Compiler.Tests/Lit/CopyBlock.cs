// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --max-ticks 20000000
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64 --max-ticks 20000000
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

using System.Runtime.CompilerServices;

// Unsafe.CopyBlock is the cpblk instruction on the host runtime. Roslyn compiles it as a call,
// so on DotNetro it is the library method that cpblk itself also lowers to.
var source = new byte[10];
var destination = new byte[10];
for (var i = 0; i < source.Length; i++)
    source[i] = (byte)(i + 1);
destination[0] = 99;
destination[9] = 99;

Copy(destination, 1, source, 2, 7);

for (var i = 0; i < destination.Length; i++)
    Console.WriteLine(destination[i]);

// A zero-length copy changes nothing.
Copy(destination, 0, source, 0, 0);
Console.WriteLine(destination[0]);

// Counts around the 256-byte page size, which the copy routine handles as whole pages plus a tail.
var big = new byte[600];
var bigSource = new byte[600];
for (var i = 0; i < bigSource.Length; i++)
    bigSource[i] = (byte)(i * 7 + 1);
Check(big, bigSource, 1);
Check(big, bigSource, 255);
Check(big, bigSource, 256);
Check(big, bigSource, 257);
Check(big, bigSource, 511);
Check(big, bigSource, 512);
Check(big, bigSource, 513);
Check(big, bigSource, 600);

// Copy `count` bytes over a buffer of 200s and report whether the first `count` bytes are the
// source's and the rest untouched.
static void Check(byte[] to, byte[] from, uint count)
{
    for (var i = 0; i < to.Length; i++)
        to[i] = 200;
    Copy(to, 0, from, 0, count);

    var correct = true;
    for (var i = 0; i < to.Length; i++)
    {
        var expected = i < count ? from[i] : (byte)200;
        if (to[i] != expected)
            correct = false;
    }
    Console.WriteLine((int)count);
    Console.WriteLine(correct);
}

static unsafe void Copy(byte[] to, int toIndex, byte[] from, int fromIndex, uint count)
{
    fixed (byte* t = &to[toIndex])
    fixed (byte* f = &from[fromIndex])
        Unsafe.CopyBlock(t, f, count);
}
