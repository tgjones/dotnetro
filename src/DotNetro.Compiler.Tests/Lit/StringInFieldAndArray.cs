// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var holder = new Holder { Name = "field" };
Console.WriteLine(holder.Name);
Console.WriteLine(holder.Name.Length);

var words = new string[3];
words[0] = "zero";
words[2] = "two";
Console.WriteLine(words[0]);
Console.WriteLine(words[2] + words[0]);
Console.WriteLine(words[1] == null);
Console.WriteLine(string.IsNullOrEmpty(words[1]));
Console.WriteLine(string.IsNullOrEmpty(""));
Console.WriteLine(string.IsNullOrEmpty(words[0]));

class Holder
{
    public string Name;
}
