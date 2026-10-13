// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
string first = "Hello";
string second = ", ";
string third = "World";
Console.WriteLine(first + second + third);
Console.WriteLine((first + second + third + "!").Length);
Console.WriteLine(first + "");
Console.WriteLine("" + "");
Console.WriteLine(Add("1", "2") + Add("3", "4"));

static string Add(string x, string y) => x + y;
