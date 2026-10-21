// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// A derived class's fields sit after its base class's, not on top of them.
var c = new Child();
c.A = 1;
c.B = 2;
c.C = 3;
Console.WriteLine(c.A);
Console.WriteLine(c.B);
Console.WriteLine(c.C);

class Parent { public int A; }
class Middle : Parent { public byte B; }
class Child : Middle { public int C; }
