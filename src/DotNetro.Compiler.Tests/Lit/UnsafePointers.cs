// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Unsafe.AsPointer: the address a managed reference refers to.
int x = 5;
unsafe
{
    int* p = (int*)Unsafe.AsPointer(ref x);
    *p = *p + 2;
}
Console.WriteLine(x);

// MemoryMarshal.GetArrayDataReference: a reference to the first element, whatever its type.
var bytes = new byte[] { 10, 20, 30 };
Console.WriteLine(MemoryMarshal.GetArrayDataReference(bytes));
Poke(bytes, 77);
Console.WriteLine(bytes[0]);

var ints = new int[] { 1, 2, 3, 4 };
Poke(ints, 9);
Console.WriteLine(ints[0]);
Console.WriteLine(ints[1]);

// Writes a byte through the data reference, whatever the array: for an int[] that is its low byte.
static void Poke(Array array, byte value)
{
    MemoryMarshal.GetArrayDataReference(array) = value;
}
