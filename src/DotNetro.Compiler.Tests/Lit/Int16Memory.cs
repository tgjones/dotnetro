// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var shorts = new short[3];
shorts[0] = -1;
shorts[1] = 1000;
shorts[2] = (short)(shorts[0] + shorts[1]);
Console.WriteLine(shorts.Length);
Console.WriteLine(shorts[0]);
Console.WriteLine(shorts[2]);
var ushorts = new ushort[2];
ushorts[0] = 65535;
ushorts[1] = 2;
Console.WriteLine(ushorts[0]);
Console.WriteLine(ushorts[0] + ushorts[1]);
var chars = new char[2];
chars[1] = (char)90;
Console.WriteLine((int)chars[0]);
Console.WriteLine((int)chars[1]);
var box = new Box { S = -7, U = 60000, C = (char)66 };
Console.WriteLine(box.S);
Console.WriteLine(box.U);
Console.WriteLine((int)box.C);
Console.WriteLine(Twice(-4));
Console.WriteLine(Widen(50000));

static short Twice(short v) => (short)(v * 2);
static int Widen(ushort v) => v + 1;

class Box
{
    public short S;
    public ushort U;
    public char C;
}
