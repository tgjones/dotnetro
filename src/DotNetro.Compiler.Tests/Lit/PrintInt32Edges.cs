// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Console.WriteLine(0);
Console.WriteLine(7);
Console.WriteLine(10);
Console.WriteLine(99);
Console.WriteLine(100);
Console.WriteLine(65535);
Console.WriteLine(65536);
Console.WriteLine(1000000000);
Console.WriteLine(2147483647);
Console.WriteLine(-2147483648);
Console.WriteLine(-1);
Console.WriteLine(-10);
Console.WriteLine(-2147483647);
Console.WriteLine(1999999999);
Console.WriteLine(1234567890);
