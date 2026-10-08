// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Console.WriteLine(Helper.Mix(250, -3));
Console.WriteLine(Helper.Bump(-7));

static class Helper
{
    public static int Mix(byte a, sbyte b) => a + b;
    public static sbyte Bump(sbyte v) => (sbyte)(v + 1);
}
