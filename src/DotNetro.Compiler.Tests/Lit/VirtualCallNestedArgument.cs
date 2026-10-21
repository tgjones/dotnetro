// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Base s = new Derived();
Console.WriteLine(s.Sq(s.Sq(4)));

class Base
{
    public virtual int Sq(int x) => x * x;
}

class Derived : Base
{
    public override int Sq(int x) => x + 1;
}
