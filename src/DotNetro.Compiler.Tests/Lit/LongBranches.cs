// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
static void Run(int n, bool flag)
{
    var i = 0;
    while (i < n)
    {
        Console.WriteLine(i + 1);
        Console.WriteLine(i + 2);
        Console.WriteLine(i + 3);
        Console.WriteLine(i + 4);
        Console.WriteLine(i + 5);
        Console.WriteLine(i + 6);
        Console.WriteLine(i + 7);
        Console.WriteLine(i + 8);
        Console.WriteLine(i + 9);
        Console.WriteLine(i + 10);
        Console.WriteLine(i + 11);
        Console.WriteLine(i + 12);
        i++;
    }
    if (flag)
    {
        Console.WriteLine(100 + 1);
        Console.WriteLine(100 + 2);
        Console.WriteLine(100 + 3);
        Console.WriteLine(100 + 4);
        Console.WriteLine(100 + 5);
        Console.WriteLine(100 + 6);
        Console.WriteLine(100 + 7);
        Console.WriteLine(100 + 8);
        Console.WriteLine(100 + 9);
        Console.WriteLine(100 + 10);
        Console.WriteLine(100 + 11);
        Console.WriteLine(100 + 12);
    }
    else
    {
        Console.WriteLine(200 + 1);
        Console.WriteLine(200 + 2);
        Console.WriteLine(200 + 3);
        Console.WriteLine(200 + 4);
        Console.WriteLine(200 + 5);
        Console.WriteLine(200 + 6);
        Console.WriteLine(200 + 7);
        Console.WriteLine(200 + 8);
        Console.WriteLine(200 + 9);
        Console.WriteLine(200 + 10);
        Console.WriteLine(200 + 11);
        Console.WriteLine(200 + 12);
    }
    if (!flag)
    {
        Console.WriteLine(300 + 1);
        Console.WriteLine(300 + 2);
        Console.WriteLine(300 + 3);
        Console.WriteLine(300 + 4);
        Console.WriteLine(300 + 5);
        Console.WriteLine(300 + 6);
        Console.WriteLine(300 + 7);
        Console.WriteLine(300 + 8);
        Console.WriteLine(300 + 9);
        Console.WriteLine(300 + 10);
        Console.WriteLine(300 + 11);
        Console.WriteLine(300 + 12);
    }
}
static void Again(int n)
{
    var i = 0;
    do
    {
        Console.WriteLine(400 + 1);
        Console.WriteLine(400 + 2);
        Console.WriteLine(400 + 3);
        Console.WriteLine(400 + 4);
        Console.WriteLine(400 + 5);
        Console.WriteLine(400 + 6);
        Console.WriteLine(400 + 7);
        Console.WriteLine(400 + 8);
        Console.WriteLine(400 + 9);
        Console.WriteLine(400 + 10);
        Console.WriteLine(400 + 11);
        Console.WriteLine(400 + 12);
        i++;
    } while (i < n);
}
Run(2, true);
Again(2);
Run(1, false);
