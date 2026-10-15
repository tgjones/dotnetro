// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
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
