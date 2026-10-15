// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int i = -5;
uint ui = 0xFFFFFFFB;
short s = -300;
byte by = 200;
sbyte sb = -3;
char ch = 'A';
long fromInt = i;
long fromUInt = ui;
long fromShort = s;
long fromByte = by;
long fromSByte = sb;
long fromChar = ch;
PrintLong(fromInt);
PrintLong(fromUInt);
PrintLong(fromShort);
PrintLong(fromByte);
PrintLong(fromSByte);
PrintLong(fromChar);
ulong uFromInt = (ulong)i;
ulong uFromUInt = ui;
PrintULong(uFromInt);
PrintULong(uFromUInt);
long big = 0x123456789ABCDEF0;
Console.WriteLine((int)big);
PrintLong((long)(uint)big);
Console.WriteLine((short)big);
Console.WriteLine((ushort)big);
Console.WriteLine((byte)big);
Console.WriteLine((sbyte)big);
Console.WriteLine((int)(big >> 32));
long neg = -big;
Console.WriteLine((int)neg);
Console.WriteLine((sbyte)neg);

// Console.WriteLine(long) is not supported yet: print the two 32-bit halves.
static void PrintLong(long v)
{
    Console.WriteLine((int)(v >> 32));
    Console.WriteLine((int)v);
}

static void PrintULong(ulong v)
{
    Console.WriteLine((int)(v >> 32));
    Console.WriteLine((int)v);
}
