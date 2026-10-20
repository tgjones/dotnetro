// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
using System;

int a = 42, n = -7, z = 0, mn = int.MinValue;
short sh = short.MinValue;
byte by = 200;
char ch = 'Z';
bool bo = true;
Console.WriteLine($"int {a} {n} {z} {mn}");
Console.WriteLine($"short {sh} byte {by} char {ch} bool {bo} {!bo}");
