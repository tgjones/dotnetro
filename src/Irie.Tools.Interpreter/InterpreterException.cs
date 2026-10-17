namespace Irie.Tools.Interpreter;

// The interpreted program did something with no defined result (division by
// zero, a shift past the width, a wild pointer, a runaway loop), or uses an
// operation the interpreter does not model.
public sealed class InterpreterException(string message) : Exception(message);
