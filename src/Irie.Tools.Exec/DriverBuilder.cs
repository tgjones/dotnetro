using System.Text;
using Irie.Dialects.Call;
using Irie.Mir;

namespace Irie.Tools.Exec;

// Builds the program that runs a function over a table of argument vectors on
// the target and prints each result as big-endian hex:
//
//   @__exec_main    loops over @__exec_vectors, calls the function, prints the result
//   @__exec_puthex  prints one byte as two hex digits through the target's output routine
//   @__exec_vectors the argument table: per vector, each argument little-endian
//   @__exec_digits  "0123456789ABCDEF"
//
// The driver is itself compiled by the pipeline under test; it sticks to loads,
// adds, a compare and calls so that a failure there shows up as every vector
// mismatching rather than going unnoticed.
internal static class DriverBuilder
{
    public const string MainName = "__exec_main";

    // `bufferSymbols` has one entry per parameter: the global a pointer parameter points
    // into (its table entry is the byte offset), or null for a plain value.
    public static void AddDriver(MirModule module, MirFunction target, IReadOnlyList<ulong[]> vectors, ExecTarget execTarget, IReadOnlyList<string?> bufferSymbols)
    {
        var argumentBytes = target.ParameterTypes.Select(ByteWidth).ToArray();
        var resultBytes = ByteWidth(target.ReturnType);
        var stride = argumentBytes.Sum();

        module.Globals.Add(new MirGlobal("__exec_digits", IRType.I8, 16,
            new MirInitializer([new DataBytes(Encoding.ASCII.GetBytes("0123456789ABCDEF"))])));

        var table = new List<byte>();
        foreach (var vector in vectors)
            for (var i = 0; i < vector.Length; i++)
                for (var b = 0; b < argumentBytes[i]; b++)
                    table.Add((byte)(vector[i] >> (8 * b)));
        module.Globals.Add(new MirGlobal("__exec_vectors", IRType.I8, table.Count,
            new MirInitializer([new DataBytes(table.ToArray())])));

        var text = new StringBuilder();
        AppendMain(text, target, execTarget, argumentBytes, bufferSymbols, resultBytes, stride, vectors.Count);
        AppendPutHex(text, execTarget);
        var driver = MirModule.Parse(new StringReader(text.ToString()));

        // The program starts at the first function, so main goes first.
        module.Functions.Insert(0, driver.Functions[0]);
        module.Functions.Add(driver.Functions[1]);
    }

    public static int ByteWidth(IRType type) => type switch
    {
        VoidType => 0,
        PointerType => type.SizeInBits / 8,
        IntegerType { SizeInBits: 8 or 16 or 32 or 64 } => type.SizeInBits / 8,
        _ => throw new NotSupportedException($"Only i8/i16/i32/i64 and pointer arguments and i8/i16/i32/i64 and void results can be driven, got {type.DisplayName}."),
    };

    private static void AppendMain(StringBuilder text, MirFunction target, ExecTarget execTarget, int[] argumentBytes, IReadOnlyList<string?> bufferSymbols, int resultBytes, int stride, int count)
    {
        var v = 0;
        string Next() => $"%{v++}";

        var table = Next();
        var zero = Next();
        var stackTop = Next();
        var stackPointer = Next();
        var stackAddress = Next();
        text.AppendLine($"func @{MainName} : () -> void {{");
        text.AppendLine("bb0():");
        // The program starts here, so it sets up the soft stack the way a real
        // program's startup does: the pointer lives in the first two zero-page registers.
        text.AppendLine($"    {stackTop} : i16 = arith.constant {execTarget.SoftStackTop}");
        text.AppendLine($"    {stackAddress} : i16 = arith.constant {execTarget.SoftStackPointerAddress}");
        text.AppendLine($"    {stackPointer} : ptr = cast.inttoptr {stackAddress}");
        text.AppendLine($"    mem.store.i16 {stackPointer}, {stackTop}");
        text.AppendLine($"    {table} : ptr = mem.symbol @__exec_vectors");
        text.AppendLine($"    {zero} : i16 = arith.constant 0");
        text.AppendLine($"    cf.br bb1({table}, {zero})");

        var pointer = Next();
        var index = Next();
        var limit = Next();
        var more = Next();
        text.AppendLine($"bb1({pointer} : ptr, {index} : i16):");
        text.AppendLine($"    {limit} : i16 = arith.constant {count}");
        text.AppendLine($"    {more} : i1 = arith.cmpi ult, {index}, {limit}");
        text.AppendLine($"    cf.cond_br {more}, bb2(), bb3()");

        text.AppendLine("bb2():");
        var arguments = new List<string>();
        var offset = 0;
        for (var a = 0; a < argumentBytes.Length; a++)
        {
            var bytes = argumentBytes[a];
            var isPointer = target.ParameterTypes[a] is PointerType;
            if (isPointer && bufferSymbols[a] is null)
                throw new NotSupportedException($"Parameter {a} of @{target.Name} is a pointer: give it a buf(N) argument.");

            var offsetValue = Next();
            var address = Next();
            var argument = Next();
            text.AppendLine($"    {offsetValue} : i16 = arith.constant {offset}");
            text.AppendLine($"    {address} : ptr = mem.ptradd {pointer}, {offsetValue}");
            if (isPointer)
            {
                // The table holds the offset into the buffer.
                var bufferOffset = Next();
                var bufferBase = Next();
                text.AppendLine($"    {bufferOffset} : i16 = mem.load.i16 {address}");
                text.AppendLine($"    {bufferBase} : ptr = mem.symbol @{bufferSymbols[a]}");
                text.AppendLine($"    {argument} : ptr = mem.ptradd {bufferBase}, {bufferOffset}");
                arguments.Add(argument);
            }
            else
            {
                text.AppendLine($"    {argument} : i{bytes * 8} = mem.load.i{bytes * 8} {address}");
                arguments.Add(argument);
            }
            offset += bytes;
        }

        var result = resultBytes == 0 ? "" : Next();
        var callArguments = string.Concat(arguments.Select(a => ", " + a));
        text.AppendLine(resultBytes == 0
            ? $"    call.func @{target.Name}{callArguments}"
            : $"    {result} : i{resultBytes * 8} = call.func @{target.Name}{callArguments}");

        for (var b = resultBytes - 1; b >= 0; b--)
        {
            var shifted = result;
            if (b > 0)
            {
                var amount = Next();
                shifted = Next();
                text.AppendLine($"    {amount} : i8 = arith.constant {8 * b}");
                text.AppendLine($"    {shifted} : i{resultBytes * 8} = arith.lshr {result}, {amount}");
            }

            var value = shifted;
            if (resultBytes > 1)
            {
                value = Next();
                text.AppendLine($"    {value} : i8 = cast.trunc {shifted}");
            }

            text.AppendLine($"    call.func @__exec_puthex, {value}");
        }

        var step = Next();
        var nextPointer = Next();
        var one = Next();
        var nextIndex = Next();
        text.AppendLine($"    {step} : i16 = arith.constant {stride}");
        text.AppendLine($"    {nextPointer} : ptr = mem.ptradd {pointer}, {step}");
        text.AppendLine($"    {one} : i16 = arith.constant 1");
        text.AppendLine($"    {nextIndex} : i16 = arith.addi {index}, {one}");
        text.AppendLine($"    cf.br bb1({nextPointer}, {nextIndex})");

        // Nothing to return to: halt in a self-branch, which the emulator detects.
        text.AppendLine("bb3():");
        text.AppendLine("    cf.br bb3()");
        text.AppendLine("}");
    }

    private static void AppendPutHex(StringBuilder text, ExecTarget execTarget)
    {
        CallConvention.TryGetName(execTarget.PutCharConvention, out var convention);
        text.AppendLine("func @__exec_puthex : (i8) -> void {");
        text.AppendLine("bb0(%0 : i8):");
        text.AppendLine("    %1 : i8 = arith.constant 4");
        text.AppendLine("    %2 : i8 = arith.lshr %0, %1");
        text.AppendLine("    %3 : i8 = arith.constant 15");
        text.AppendLine("    %4 : i8 = arith.and %0, %3");
        text.AppendLine("    %5 : i16 = cast.zext %2");
        text.AppendLine("    %6 : i16 = cast.zext %4");
        text.AppendLine("    %7 : ptr = mem.symbol @__exec_digits");
        text.AppendLine("    %8 : ptr = mem.ptradd %7, %5");
        text.AppendLine("    %9 : i8 = mem.load.i8 %8");
        text.AppendLine($"    %10 : i16 = arith.constant {execTarget.PutCharAddress}");
        text.AppendLine("    %11 : ptr = cast.inttoptr %10");
        text.AppendLine($"    call.indirect %11, {convention}, %9");
        text.AppendLine("    %12 : ptr = mem.ptradd %7, %6");
        text.AppendLine("    %13 : i8 = mem.load.i8 %12");
        text.AppendLine($"    call.indirect %11, {convention}, %13");
        text.AppendLine("    core.return");
        text.AppendLine("}");
    }
}
