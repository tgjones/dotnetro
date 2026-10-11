// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Console.WriteLine(Less((byte)200, (byte)5));
Console.WriteLine(Less((byte)5, (byte)200));
Console.WriteLine(LessS((sbyte)-3, (sbyte)4));
Console.WriteLine(LessS((sbyte)4, (sbyte)-3));
Console.WriteLine(LessU16(65535, 5));
Console.WriteLine(LessU16(5, 65535));
Console.WriteLine(LessS16(-30000, 20000));
Console.WriteLine(LessS16(20000, -30000));
Console.WriteLine(Mixed(-1, 65535));
Console.WriteLine(AndS16(-1, -256));
Console.WriteLine(AndU16(65535, 255));

static int Less(byte a, byte b) { if (a < b) return 1; return 0; }
static int LessS(sbyte a, sbyte b) { if (a < b) return 1; return 0; }
static int LessU16(ushort a, ushort b) { if (a < b) return 1; return 0; }
static int LessS16(short a, short b) { if (a < b) return 1; return 0; }
static int Mixed(short a, ushort b) { if (a == b) return 1; return 0; }
static int AndS16(short a, short b) => a & b;
static int AndU16(ushort a, ushort b) => a & b;
