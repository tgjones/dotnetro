// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var a = new int[3];
Fill(a, 3);
Console.WriteLine(Sum(a));

static void Fill(int[] array, int value)
{
    for (int i = 0; i < array.Length; i++)
        array[i] = value + i;
}

static int Sum(int[] array)
{
    int total = 0;
    for (int i = 0; i < array.Length; i++)
        total += array[i];
    return total;
}
