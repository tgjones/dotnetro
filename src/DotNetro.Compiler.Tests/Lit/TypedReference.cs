// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// __makeref is mkrefany and __refvalue is refanyval.
int x = 5;
TypedReference reference = __makeref(x);
__refvalue(reference, int) = 9;
Console.WriteLine(x);
Console.WriteLine(__refvalue(reference, int));

// Passed to a method, it reaches the original variable.
Bump(__makeref(x));
Console.WriteLine(x);

byte small = 1;
SetByte(__makeref(small));
Console.WriteLine(small);

// The target can be a field or an array element.
var box = new Box();
Bump(__makeref(box.Value));
Console.WriteLine(box.Value);

var numbers = new int[] { 10, 20, 30, 40 };
Bump(__makeref(numbers[2]));
Console.WriteLine(numbers[1]);
Console.WriteLine(numbers[2]);

static void Bump(TypedReference reference)
{
    __refvalue(reference, int) += 100;
}

static void SetByte(TypedReference reference)
{
    __refvalue(reference, byte) = 200;
}

class Box
{
    public int Value = 7;
}
