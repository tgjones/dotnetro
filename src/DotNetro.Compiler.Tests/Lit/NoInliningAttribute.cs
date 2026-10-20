// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// A single-call-site helper marked NoInlining must still behave the same (the compiler keeps it a function of its own).
using System.Runtime.CompilerServices;

Console.WriteLine(Twice(21));

[MethodImpl(MethodImplOptions.NoInlining)]
static int Twice(int x) => x + x;
