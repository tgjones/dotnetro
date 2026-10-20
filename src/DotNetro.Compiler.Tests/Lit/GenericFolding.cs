// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var a = new Stack<A>(1); a.Push(new A()); Console.WriteLine(a.Count);
a.Push(null); a.Push(null); Console.WriteLine(a.Pop() == null);
var b = new Stack<B>(1); b.Push(new B()); Console.WriteLine(b.Count);
b.Push(null); b.Push(null); Console.WriteLine(b.Pop() == null);
var c = new Stack<C>(1); c.Push(new C()); Console.WriteLine(c.Count);
c.Push(null); c.Push(null); Console.WriteLine(c.Pop() == null);
var d = new Stack<string>(1); d.Push("x"); Console.WriteLine(d.Pop());
d.Push("y"); d.Push("z"); Console.WriteLine(d.Pop() == null);
class A {} class B {} class C {}
class Stack<T>
{
    private T[] _items; private int _count;
    public Stack(int capacity) { _items = new T[capacity]; }
    public int Count => _count;
    public void Push(T item) { if (_count == _items.Length) Grow(); _items[_count++] = item; }
    public T Pop() => _items[--_count];
    private void Grow() { var n = new T[_items.Length * 2]; for (var i = 0; i < _count; i++) n[i] = _items[i]; _items = n; }
}
