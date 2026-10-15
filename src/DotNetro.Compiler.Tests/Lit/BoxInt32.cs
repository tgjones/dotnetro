// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int x = 41;
object o = x;
x = 99;
Console.WriteLine((int)o);
Console.WriteLine((int)o + 1);
Console.WriteLine(o.ToString());
Console.WriteLine(o.Equals(41));
Console.WriteLine(o.Equals(42));

object neg = -1234;
Console.WriteLine(neg.ToString());
Console.WriteLine(neg.GetHashCode());
