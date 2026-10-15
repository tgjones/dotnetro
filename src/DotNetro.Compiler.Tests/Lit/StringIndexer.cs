// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
string text = "ABC xyz";
Console.WriteLine(text[0]);
Console.WriteLine((int)text[1]);
var sum = 0;
for (var i = 0; i < text.Length; i++)
    sum += text[i];
Console.WriteLine(sum);
