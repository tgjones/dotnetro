// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Console.WriteLine(Helper.Mix(250, -3));
Console.WriteLine(Helper.Bump(-7));

static class Helper
{
    public static int Mix(byte a, sbyte b) => a + b;
    public static sbyte Bump(sbyte v) => (sbyte)(v + 1);
}
