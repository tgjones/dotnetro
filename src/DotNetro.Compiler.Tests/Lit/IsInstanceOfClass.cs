// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Animal a = new Dog();
object o = a;
Console.WriteLine(o is Dog);
Console.WriteLine(o is Animal);
Console.WriteLine(o is Cat);
Console.WriteLine(o is object);
Console.WriteLine(o is string);
object s = "text";
Console.WriteLine(s is string);
Console.WriteLine(s is Animal);
object n = null;
Console.WriteLine(n is Animal);
Console.WriteLine(n is string);
Animal lab = new Labrador();
Console.WriteLine(lab is Dog);
Console.WriteLine(lab is Labrador);
Console.WriteLine(a is Labrador);
var asDog = o as Dog;
Console.WriteLine(asDog != null);
var asCat = o as Cat;
Console.WriteLine(asCat == null);
if (o is Dog dog)
    Console.WriteLine(dog.Speak());

class Animal { }
class Dog : Animal { public int Speak() => 42; }
class Labrador : Dog { }
class Cat : Animal { }
