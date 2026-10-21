// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// An array initializer of constants is `ldtoken` of the data field plus RuntimeHelpers.InitializeArray.
int[] ints = { 1, 2, 3, 1000, -5, 70000 };
byte[] bytes = { 9, 8, 7, 6, 5, 255 };
ushort[] ushorts = { 1, 258, 65535, 4 };
sbyte[] sbytes = { -1, -2, 3, 4 };
bool[] bools = { true, false, true, true };

PrintAll(ints);
foreach (var b in bytes)
    Console.WriteLine(b);
foreach (var u in ushorts)
    Console.WriteLine(u);
foreach (var s in sbytes)
    Console.WriteLine(s);
foreach (var b in bools)
    Console.WriteLine(b);
Console.WriteLine(ints.Length);

// The array is a fresh copy each time the initializer runs.
var first = Make();
first[0] = 99;
Console.WriteLine(Make()[0]);

static void PrintAll(int[] values)
{
    foreach (var v in values)
        Console.WriteLine(v);
}

static int[] Make() => new int[] { 10, 20, 30, 40 };
