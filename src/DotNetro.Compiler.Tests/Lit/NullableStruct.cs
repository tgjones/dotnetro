// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
Pair? a = new Pair { A = 1, B = 2 };
Console.WriteLine(a.HasValue);
Console.WriteLine(a.Value.B);
Pair? none = null;
Console.WriteLine(none.HasValue);
Console.WriteLine(Describe(a));
Console.WriteLine(Describe(none));

object boxed = a;
Console.WriteLine(boxed is Pair);
Pair? back = (Pair?)boxed;
Console.WriteLine(back.Value.A);

int? n = 4;
Console.WriteLine(Twice(n).Value);
Console.WriteLine(Twice(null).HasValue);

static int Describe(Pair? value) => value.HasValue ? value.Value.A + value.Value.B : -1;

static int? Twice(int? value) => value.HasValue ? value.Value * 2 : null;

struct Pair
{
    public int A;
    public int B;
}
