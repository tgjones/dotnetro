// RUN-dotnet: @cs_compiler @file | @dotnet_runner --input "Hello there"
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --input "Hello there"
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64 --input "Hello there"
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var line = Console.ReadLine();
Console.WriteLine(line.Length);
Console.WriteLine(line[1]);
Console.WriteLine(line + "!");
Console.WriteLine(line == "Hello there");
