// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var items = new Item[2];
items[1] = new Item { Value = 42 };
Console.WriteLine(items[0] == null ? 1 : 0);
Console.WriteLine(items[1] == null ? 1 : 0);
Console.WriteLine(items[1] != null ? items[1].Value : -1);
Item? none = null;
if (none == null)
    Console.WriteLine(5);
if (items[1] != null)
    Console.WriteLine(6);

class Item
{
    public int Value;
}
