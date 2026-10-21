namespace System;

/// <summary>
/// A delegate is an object holding a target and the address of the code to run on it. The compiler
/// fills the fields at <c>newobj</c> and expands <c>Invoke</c> into a call of the address with the
/// target as its first argument; a delegate over a static method points at a thunk that drops the
/// target. Neither the constructors nor <c>Invoke</c> of a delegate type have a body.
/// </summary>
public abstract class Delegate
{
    internal object _target;
    internal unsafe byte* _methodPtr;
}

public abstract class MulticastDelegate : Delegate
{
}
