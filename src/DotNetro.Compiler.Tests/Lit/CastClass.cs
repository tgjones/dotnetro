// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Animal a = new Dog();
Dog d = (Dog)a;
Console.WriteLine(d.Speak());
object o = d;
Animal back = (Animal)o;
Console.WriteLine(back == a);
object nothing = null;
Console.WriteLine((Dog)nothing == null);
string s = (string)(object)"hello";
Console.WriteLine(s);
Console.WriteLine(s.Length);

class Animal { }
class Dog : Animal { public int Speak() => 7; }
