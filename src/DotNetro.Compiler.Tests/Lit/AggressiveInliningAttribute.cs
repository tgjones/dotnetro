// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// A helper with several call sites and a body too big to be inlined by default, marked AggressiveInlining.
using System.Runtime.CompilerServices;

Console.WriteLine(Mix(1, 2));
Console.WriteLine(Mix(30, 4));
Console.WriteLine(Mix(100, 50));

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static int Mix(int a, int b)
{
    var t = a * 3 + b;
    for (var i = 0; i < b; i++)
        t += a ^ i;
    if (t > 200) t -= 200;
    return t;
}
