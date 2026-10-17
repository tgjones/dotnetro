using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Irie.Mir;
using Irie.Passes;
using Irie.Tools.Exec;
using Irie.Tools.Interpreter;

var inputArgument = new Argument<FileInfo>("input") { Description = "Path to the input MIR file" };
var execOption = new Option<string[]>("--exec") { Description = "A function to run and the values to run it with, e.g. \"add_i16(*, *)\". Each argument is * (edge values), lo..hi (every value) or a|b|c. May be repeated.", Required = true, AllowMultipleArgumentsPerToken = false };
var targetOption = new Option<string>("--target") { Description = "Target to compile for and emulate (as for iriec; only 'mos6502-bbcmicro' so far)", Required = true };
var emulatorOption = new Option<FileInfo>("--emulator") { Description = "Path to the DotNetro 6502 emulator executable", Required = true };
var maxTicksOption = new Option<int>("--max-ticks") { Description = "Emulator tick budget per function. Default: 50,000,000.", DefaultValueFactory = _ => 50_000_000 };

var rootCommand = new RootCommand("Irie execution checker -- runs a MIR function over generated inputs in the interpreter and, compiled, in the emulator, and compares the results");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(execOption);
rootCommand.Options.Add(emulatorOption);
rootCommand.Options.Add(maxTicksOption);

rootCommand.SetAction(parseResult =>
{
    var input = parseResult.GetValue(inputArgument)!;
    ExecTarget execTarget;
    try
    {
        execTarget = ExecTarget.Get(parseResult.GetValue(targetOption)!);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    var emulator = parseResult.GetValue(emulatorOption)!;
    var maxTicks = parseResult.GetValue(maxTicksOption);

    var text = File.ReadAllText(input.FullName);
    var directives = parseResult.GetValue(execOption)!.Select(ExecDirective.Parse).ToList();

    var failed = false;
    foreach (var directive in directives)
        failed |= !Check(text, directive, execTarget, emulator, maxTicks);
    return failed ? 1 : 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static bool Check(string text, ExecDirective directive, ExecTarget execTarget, FileInfo emulator, int maxTicks)
{
    // The interpreter reads a module of its own: the pipeline rewrites a module
    // in place, and the oracle must not see any of that.
    var reference = MirModule.Parse(new StringReader(text));
    var function = reference.Functions.FirstOrDefault(f => f.Name == directive.Function);
    if (function is null)
    {
        Console.Error.WriteLine($"--exec: no function @{directive.Function} in the file.");
        return false;
    }
    if (directive.Arguments.Count != function.ParameterTypes.Length)
    {
        Console.Error.WriteLine($"--exec: @{directive.Function} takes {function.ParameterTypes.Length} argument(s), the directive gives {directive.Arguments.Count}.");
        return false;
    }

    var widths = function.ParameterTypes.Select(t => DriverBuilder.ByteWidth(t) * 8).ToArray();
    var resultBytes = DriverBuilder.ByteWidth(function.ReturnType);
    var vectors = VectorGenerator.Generate(directive.Arguments, widths);

    var interpreter = new MirInterpreter(reference);
    var expected = new List<ulong>();
    foreach (var vector in vectors)
    {
        try
        {
            expected.Add(interpreter.Call(directive.Function, vector)!.Value);
        }
        catch (InterpreterException ex)
        {
            Console.Error.WriteLine($"@{directive.Function}({Format(vector, widths)}): the interpreter rejected this input: {ex.Message}");
            return false;
        }
    }

    ulong[]? actual;
    string error;
    try
    {
        actual = RunCompiled(text, directive.Function, vectors, resultBytes, execTarget, emulator, maxTicks, out error);
    }
    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
    {
        // The pipeline rejected the program (an unsupported construct, an
        // unresolved libcall): the compiler under test failed, not the harness.
        actual = null;
        error = $"compilation failed: {ex.Message}";
    }
    if (actual is null)
    {
        Console.Error.WriteLine($"@{directive.Function}: {error}");
        return false;
    }

    var mismatches = 0;
    for (var i = 0; i < vectors.Count; i++)
    {
        if (actual[i] == expected[i])
            continue;
        if (++mismatches <= 10)
            Console.Error.WriteLine($"@{directive.Function}({Format(vectors[i], widths)}): compiled returned {Hex(actual[i], resultBytes)}, interpreter returned {Hex(expected[i], resultBytes)}");
    }

    if (mismatches > 0)
    {
        Console.Error.WriteLine($"@{directive.Function}: {mismatches} of {vectors.Count} vectors mismatched.");
        return false;
    }

    Console.WriteLine($"@{directive.Function}: {vectors.Count} vectors match");
    return true;
}

// Compiles the module plus a driver for the function and runs it in the
// emulator; returns one result per vector, or null with an explanation.
static ulong[]? RunCompiled(string text, string functionName, List<ulong[]> vectors, int resultBytes, ExecTarget execTarget, FileInfo emulator, int maxTicks, out string error)
{
    var target = execTarget.Create();
    var module = MirModule.Parse(new StringReader(text));
    var function = module.Functions.First(f => f.Name == functionName);
    KeepReachable(module, function);
    DriverBuilder.AddDriver(module, function, vectors, execTarget);

    var passManager = new PassManager(null, null);
    PipelineBuilder.AddStandardPipeline(passManager, target);
    passManager.Run(new CompilationContext(module));

    var origin = target.DefaultOrigin!.Value;
    var program = target.BuildProgram(module, origin);

    var directory = Directory.CreateTempSubdirectory("irie-exec-");
    try
    {
        var programPath = Path.Combine(directory.FullName, "program.bin");
        File.WriteAllBytes(programPath, program);

        var startInfo = new ProcessStartInfo(emulator.FullName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(programPath);
        startInfo.ArgumentList.Add("--target-system");
        startInfo.ArgumentList.Add(execTarget.EmulatorSystem);
        startInfo.ArgumentList.Add("--max-ticks");
        startInfo.ArgumentList.Add(maxTicks.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            error = $"emulator exited with code {process.ExitCode}: {stderr.Result.Trim()}";
            return null;
        }

        var digits = stdout.Trim();
        if (digits.Length != vectors.Count * resultBytes * 2)
        {
            error = $"expected {vectors.Count * resultBytes * 2} hex digits of output, got {digits.Length}: {digits}";
            return null;
        }

        error = string.Empty;
        return Enumerable.Range(0, vectors.Count)
            .Select(i => ulong.Parse(digits.AsSpan(i * resultBytes * 2, resultBytes * 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            .ToArray();
    }
    finally
    {
        directory.Delete(recursive: true);
    }
}

static string Format(ulong[] vector, int[] widths) =>
    string.Join(", ", vector.Select((v, i) => Hex(v, widths[i] / 8)));

static string Hex(ulong value, int bytes) => "0x" + value.ToString("X" + bytes * 2, CultureInfo.InvariantCulture);

// Drops every function the one under test cannot reach, so a file of several
// functions (each with its own --exec) compiles only the one being run.
static void KeepReachable(MirModule module, MirFunction root)
{
    var byName = module.Functions.ToDictionary(f => f.Name);
    var reachable = new HashSet<MirFunction> { root };
    var pending = new Stack<MirFunction>([root]);
    while (pending.Count > 0)
    {
        foreach (var block in pending.Pop().Blocks)
            foreach (var instruction in block.Instructions)
                foreach (var operand in instruction.Operands)
                    if (operand is Irie.Mir.Symbol symbol && byName.TryGetValue(symbol.Name, out var callee) && reachable.Add(callee))
                        pending.Push(callee);
    }
    module.Functions.RemoveAll(f => !reachable.Contains(f));
}
