// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
// A pointer minus a constant, after an early return and before a loop: the i32 and
// i16 zero constants of the subtraction were once merged by HoistCommonCode.
unsafe
{
    var data = new byte[4];
    fixed (byte* p = data)
    {
        *(ushort*)p = 0;
        Console.WriteLine(Chain(null) == null);
        Console.WriteLine(Chain(p + 2) == p);
    }
}

static unsafe byte* Chain(byte* obj)
{
    if (obj == null)
        return null;

    var link = *(byte**)(obj - 2);
    while (link != null)
        link = *(byte**)link;
    return obj - 2;
}
