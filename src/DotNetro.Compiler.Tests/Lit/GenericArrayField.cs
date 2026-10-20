// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var s = new Stack<int>(4);
s.Push(10);
s.Push(20);
s.Push(30);
Console.WriteLine(s.Count);
Console.WriteLine(s.Pop());
Console.WriteLine(s.Pop());
Console.WriteLine(s.Count);

var t = new Stack<byte>(2);
t.Push(250);
Console.WriteLine(t.Pop());

class Stack<T>
{
    private readonly T[] _items;
    private int _count;

    public Stack(int capacity)
    {
        _items = new T[capacity];
    }

    public int Count => _count;

    public void Push(T item)
    {
        _items[_count++] = item;
    }

    public T Pop() => _items[--_count];
}
