// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
using System;

Print($"passed {Twice(4)} and {Twice(5) + 1}");
string none = null;
Print($"null<{none}>");
Print($"{1}{2}{3}");
Print($"{Twice(1)}");
Print($"x{""}y");
string longer = "this string is much longer than the size hint of the handler";
Print($"<{longer}> and <{longer}>");
for (var i = 0; i < 3; i++)
    Print($"loop {i}");

static int Twice(int x) => x * 2;
static void Print(string s) => Console.WriteLine(s);
