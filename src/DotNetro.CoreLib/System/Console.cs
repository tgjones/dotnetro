namespace System;

// The members user programs call, with the same names and signatures as the
// reference assembly. The bodies are never translated: the IL→MIR translator
// routes each call to a runtime function by name (System_Console_WriteLine_String
// → DotNetro.Runtime.ConsoleHelper.WriteLineString, System_Console_ReadLine →
// @ReadLine in runtime.irie, …). Stage 2 of notes/custom-corelib-plan.md turns
// these into ordinary C#.
public static class Console
{
    public static void WriteLine(string value) { }

    public static void WriteLine(int value) { }

    public static void WriteLine(bool value) { }

    public static string ReadLine() => null;

    public static void Beep() { }
}
