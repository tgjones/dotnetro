// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Color c = Color.Green;
Console.WriteLine((int)c);
Console.WriteLine(c == Color.Green);
Console.WriteLine(c != Color.Red);
c = Color.Blue;
Console.WriteLine((int)c);
Console.WriteLine(Describe(Color.Red));
Console.WriteLine(Describe(c));
Console.WriteLine((int)Flags.A | (int)Flags.C);
Flags f = Flags.A | Flags.B;
Console.WriteLine((int)f);
Console.WriteLine((f & Flags.B) != 0);
Console.WriteLine((byte)Small.Max);
Console.WriteLine((long)Wide.Big == 5000000000L);
var h = new Holder { Color = Color.Blue };
Console.WriteLine(h.Color == Color.Blue);
var arr = new Color[2];
arr[1] = Color.Green;
Console.WriteLine((int)arr[0] + (int)arr[1]);

static int Describe(Color c) => c switch
{
    Color.Red => 10,
    Color.Green => 20,
    _ => 30,
};

enum Color { Red, Green, Blue }

[Flags]
enum Flags { A = 1, B = 2, C = 4 }

enum Small : byte { Min = 1, Max = 250 }

enum Wide : long { Big = 5000000000L }

class Holder { public Color Color; }
