// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var items = new Item[] { new Item { Value = 7 }, new Item { Value = 9 } };
ref readonly Item first = ref items[0];
ref readonly Item second = ref items[1];
Console.WriteLine(first.Value + second.Value);

class Item
{
    public int Value;
}
