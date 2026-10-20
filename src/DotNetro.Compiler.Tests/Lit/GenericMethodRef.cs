// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new Item { Id = 1 };
var b = new Item { Id = 2 };
Console.WriteLine(Pick(true, a, b).Id);
Console.WriteLine(Pick(false, a, b).Id);
Console.WriteLine(Pick(false, "first", "second"));

static T Pick<T>(bool first, T a, T b) => first ? a : b;

class Item
{
    public int Id;
}
