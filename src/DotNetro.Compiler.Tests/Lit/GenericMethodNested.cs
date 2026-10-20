// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Outer(5));
Console.WriteLine(Outer((byte)7));
Console.WriteLine(Outer(true));

static T Outer<T>(T value) => Middle(value, value);

static U Middle<U>(U a, U b) => Inner(a);

static T Inner<T>(T value) => value;
