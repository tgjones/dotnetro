// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var items = new Item[3];
Console.WriteLine(items.Length);
items[1] = new Item { Value = 42 };
items[2] = new Item { Value = 7 };
Console.WriteLine(items[1].Value + items[2].Value);

class Item
{
    public int Value;
}
