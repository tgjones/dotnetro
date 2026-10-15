// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
object b = true;
object y = (byte)200;
object s = (short)-300;
object l = 5000000000L;
object ch = 'Q';
Console.WriteLine((bool)b);
Console.WriteLine(b.ToString());
Console.WriteLine((byte)y);
Console.WriteLine((short)s);
Console.WriteLine(l is long);
Console.WriteLine((long)l == 5000000000L);
Console.WriteLine((char)ch == 'Q');
