// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
string empty = "";
string hello = "Hello";
Console.WriteLine(empty.Length);
Console.WriteLine(hello.Length);
Console.WriteLine("Hello, World!".Length);
// A character above 0xFF is stored as '?' on the target, but still counts as one character.
Console.WriteLine("a€b".Length);
Console.WriteLine((int)"a€b"[0]);
Console.WriteLine((int)"a€b"[2]);
