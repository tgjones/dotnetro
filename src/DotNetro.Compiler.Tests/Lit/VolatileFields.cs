// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
unsafe
{
    Counter.Ticks = 1;
    Counter.Ticks = Counter.Ticks + 1;
    Console.WriteLine(Counter.Ticks);

    var c = new Flag();
    c.Ready = true;
    c.Level = 300;
    Console.WriteLine(c.Ready ? 1 : 0);
    Console.WriteLine(c.Level);

    // Spin until the flag is seen; it is already set, so this exits at once.
    while (!c.Ready) { }
    Console.WriteLine("done");
}

static class Counter
{
    public static volatile int Ticks;
}

class Flag
{
    public volatile bool Ready;
    public volatile int Level;
}
