// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int? a = 5;
int? b = 7;
int? none = null;

Console.WriteLine(a + b);
Console.WriteLine(a + none);
Console.WriteLine((a + b).Value);
Console.WriteLine((a + none).HasValue);
Console.WriteLine(a < b);
Console.WriteLine(a == b);
Console.WriteLine(none == null);
Console.WriteLine(a != null);
Console.WriteLine(none ?? 99);
Console.WriteLine(a ?? 99);
Console.WriteLine(a.GetValueOrDefault());
Console.WriteLine(none.GetValueOrDefault(4));

byte? c = 250;
Console.WriteLine(c.HasValue ? c.Value : 0);

var holder = new Holder { Count = 3 };
Console.WriteLine(holder.Count.Value);
holder.Count = null;
Console.WriteLine(holder.Count.HasValue);

class Holder
{
    public int? Count;
}
