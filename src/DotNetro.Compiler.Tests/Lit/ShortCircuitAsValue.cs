// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
// && and || used as values: the compare of the right operand is still pending on the
// eval stack where the two paths join.
Console.WriteLine(Both(1, 1));
Console.WriteLine(Both(1, 2));
Console.WriteLine(Both(2, 1));
Console.WriteLine(Either(0, 5));
Console.WriteLine(Either(5, 0));
Console.WriteLine(Either(0, 0));
Console.WriteLine(Mixed(3, 4, 5));
Console.WriteLine(Mixed(3, 4, 6));
Console.WriteLine(Mixed(0, 4, 5));
Console.WriteLine(Under(1, 1, 3));
Console.WriteLine(Under(1, 2, 3));
Console.WriteLine(Under(1, 1, 1));
Console.WriteLine(Under(1, 2, 1));
Console.WriteLine(Switched(1, 1, 2));
Console.WriteLine(Switched(1, 2, 2));
Console.WriteLine(Switched(2, 2, 0));
object o = new Custom(7);
Console.WriteLine(o.Equals(new Custom(7)));
Console.WriteLine(o.Equals(new Custom(8)));
Console.WriteLine(o.Equals("seven"));

static bool Both(int a, int b) => a == 1 && b == 1;

static bool Either(int a, int b) => a > 0 || b > 0;

// A compare pending beneath a conditional expression, whose test is a compare-and-branch.
static bool Under(int a, int b, int k) => (a == b) == (k > 2 ? true : false);

// A compare pending beside a switch expression (Roslyn spills the stack around it).
static bool Switched(int a, int b, int k) => (a == b) == (k switch { 0 => true, 1 => true, _ => false });

static bool Mixed(int a, int b, int c) => a != 0 && (b == 4 || c == 4) && c == 5;

class Custom(int value)
{
    public int Value = value;

    public override bool Equals(object obj) => obj is Custom other && other.Value == Value;

    public override int GetHashCode() => Value;
}
