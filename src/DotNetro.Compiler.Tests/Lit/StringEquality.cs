// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
string a = "same";
string b = "sa" + "me";
string c = Join("sa", "me");
Console.WriteLine(a == c);
Console.WriteLine(a != c);
Console.WriteLine(a == "other");
Console.WriteLine(a == "sam");
Console.WriteLine(a.Equals(c));
Console.WriteLine(string.Equals(a, "Same"));
Console.WriteLine(a == null);
Console.WriteLine(b == a);

static string Join(string x, string y) => x + y;
