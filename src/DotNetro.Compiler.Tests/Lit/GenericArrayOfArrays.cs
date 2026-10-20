// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-emulated-c64: @cs_compiler @file | @dnrc --target c64 --emit program | @emulator --target-system c64
// DIFF: dotnet emulated
// DIFF: dotnet emulated-c64
var s = new Stack<int[]>(4);
var a = new int[3];
a[0] = 7;
a[2] = 9;
var b = new int[1];
b[0] = 42;
s.Push(a);
s.Push(b);
Console.WriteLine(s.Count);
Console.WriteLine(s.Pop()[0]);
var c = s.Pop();
Console.WriteLine(c[0] + c[2]);
Console.WriteLine(s.Count);

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
