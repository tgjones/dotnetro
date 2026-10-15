// RUN-dotnet: @cs_compiler @file | @dotnet_runner --input "Foo"
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --input "Foo"
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64 --input "Foo"
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine("Enter some text:");
var text = Console.ReadLine();
Console.WriteLine("You said:");
Console.WriteLine(text);
Console.Beep();
