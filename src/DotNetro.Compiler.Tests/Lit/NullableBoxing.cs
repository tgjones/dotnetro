// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
int? a = 5;
int? none = null;
byte? c = 250;

object boxedA = a;
object boxedNone = none;
object boxedC = c;
Console.WriteLine(boxedA != null);
Console.WriteLine(boxedNone == null);
Console.WriteLine(boxedA is int);
Console.WriteLine(boxedA is byte);
Console.WriteLine(boxedC is byte);
Console.WriteLine((int)boxedA);
Console.WriteLine((byte)boxedC);

int? backA = (int?)boxedA;
int? backNone = (int?)boxedNone;
Console.WriteLine(backA.HasValue);
Console.WriteLine(backA.Value);
Console.WriteLine(backNone.HasValue);

object fromValue = 12;
int? widened = (int?)fromValue;
Console.WriteLine(widened.Value);
