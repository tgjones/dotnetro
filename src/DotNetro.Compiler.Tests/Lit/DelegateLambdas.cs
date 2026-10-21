// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64

// A non-capturing lambda.
Func<int, int> square = x => x * x;
Console.WriteLine(square(7));

// A lambda capturing a local, which it also modifies.
var total = 100;
Action<int> add = n => total += n;
add(5);
add(20);
Console.WriteLine(total);

// A lambda capturing a parameter, returned from a method.
var addTen = Make(10);
Console.WriteLine(addTen(1));
Console.WriteLine(Make(20)(1));

// A delegate returning a struct, and one taking a struct.
Func<int, Pair> pair = i => new Pair { A = i, B = i + 1 };
var p = pair(3);
Console.WriteLine(p.A + p.B);
Func<Pair, int> sum = q => q.A + q.B;
Console.WriteLine(sum(p));

// Delegates in a field and an array, and a predicate.
var holder = new Holder();
holder.Callback = () => Console.WriteLine("first");
holder.Callback();
holder.Callback = () => Console.WriteLine("second");
holder.Callback();

Predicate<int> isEven = n => n % 2 == 0;
Console.WriteLine(isEven(4));
Console.WriteLine(isEven(5));

Func<int, int>[] steps = [x => x + 1, x => x * 3];
Console.WriteLine(steps[0](4));
Console.WriteLine(steps[1](4));

static Func<int, int> Make(int offset) => x => x + offset;

struct Pair { public int A; public int B; }

class Holder { public Action Callback; }
