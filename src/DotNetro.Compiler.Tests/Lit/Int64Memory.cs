// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Holder h = new Holder();
h.Value = 0x1122334455667788;
h.Other = -2;
PrintLong(h.Value);
PrintLong(h.Other);
Holder.Shared = 0x0A0B0C0D0E0F1011;
PrintLong(Holder.Shared + h.Value);

long[] array = new long[4];
for (int i = 0; i < array.Length; i++)
    array[i] = (long)i * 0x100000001L;
long total = 0;
for (int i = 0; i < array.Length; i++)
    total += array[i];
PrintLong(total);
PrintLong(array[3]);

Pair p;
p.First = 1L << 40;
p.Second = -1L;
PrintLong(p.First + p.Second);

long local = 77;
unsafe
{
    long* q = &local;
    Bump(q);
    Bump(q);
}
PrintLong(local);

static unsafe void Bump(long* x) { *x = *x * 2 + 0x100000000L; }

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

class Holder
{
    public long Value;
    public long Other;
    public static long Shared;
}

struct Pair
{
    public long First;
    public long Second;
}
