using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Irie.Mir;
using Irie.Passes;
using Irie.Tools.Exec;
using Irie.Tools.Interpreter;

var inputArgument = new Argument<FileInfo>("input") { Description = "Path to the input MIR file" };
var execOption = new Option<string[]>("--exec") { Description = "A function to run and the values to run it with, e.g. \"add_i16(*, *)\". Each argument is * (edge values), lo..hi (every value) or a|b|c. May be repeated.", Required = true, AllowMultipleArgumentsPerToken = false };
var targetOption = new Option<string>("--target") { Description = "Target to compile for and emulate (as for iriec; only 'mos6502-bbcmicro' so far)", Required = true };
var emulatorOption = new Option<FileInfo>("--emulator") { Description = "Path to the DotNetro 6502 emulator executable", Required = true };
var maxTicksOption = new Option<int>("--max-ticks") { Description = "Emulator tick budget per function. Default: 50,000,000.", DefaultValueFactory = _ => 50_000_000 };

var beamWidthOption = new Option<int?>("--beam-width")
{
    Description = "States the placement search keeps per operation (default 8; 1 is a greedy placement)",
};

var chooseConventionsOption = new Option<bool>("--choose-conventions")
{
    Description = "Let the placement of a function that is only called directly choose which of A and X its arguments arrive in and its results leave in (default: every function uses the default calling convention)",
};

var tuneOption = new Option<string?>("--tune")
{
    Description = "Placement search knobs, comma separated: beam=N (0 keeps every state: the oracle), size-weight=N, speed-weight=N, lookahead=N, freq-base=N, choose-conventions",
};

var optimizeSizeOption = new Option<bool>("--optimize-size") { Description = "Weigh bytes over cycles in the placement search (size-weight=8, speed-weight=1)" };

var optimizeSpeedOption = new Option<bool>("--optimize-speed") { Description = "Weigh cycles over bytes in the placement search (size-weight=1, speed-weight=8)" };

var rootCommand = new RootCommand("Irie execution checker -- runs a MIR function over generated inputs in the interpreter and, compiled, in the emulator, and compares the results");
rootCommand.Arguments.Add(inputArgument);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(execOption);
rootCommand.Options.Add(emulatorOption);
rootCommand.Options.Add(maxTicksOption);
rootCommand.Options.Add(beamWidthOption);
rootCommand.Options.Add(chooseConventionsOption);
rootCommand.Options.Add(tuneOption);
rootCommand.Options.Add(optimizeSizeOption);
rootCommand.Options.Add(optimizeSpeedOption);

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
    ExecSettings.Tuning = PlacementTuningCli.Resolve(parseResult.GetValue(tuneOption), parseResult.GetValue(optimizeSizeOption), parseResult.GetValue(optimizeSpeedOption), parseResult.GetValue(beamWidthOption), parseResult.GetValue(chooseConventionsOption));

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

    var bufferSymbols = AddBuffers(reference, directive);
    var widths = function.ParameterTypes.Select(t => DriverBuilder.ByteWidth(t) * 8).ToArray();
    var resultBytes = DriverBuilder.ByteWidth(function.ReturnType);
    var vectors = VectorGenerator.Generate(directive.Arguments, widths);

    var interpreter = new MirInterpreter(reference);
    var expected = new List<ulong>();
    foreach (var vector in vectors)
    {
        try
        {
            // A buffer argument's vector entry is an offset; the interpreter takes the address.
            var arguments = vector.Select((v, i) => bufferSymbols[i] is { } symbol ? v + (ulong)interpreter.GetGlobalAddress(symbol) : v).ToArray();
            expected.Add(interpreter.Call(directive.Function, arguments) ?? 0);
        }
        catch (InterpreterException ex)
        {
            Console.Error.WriteLine($"@{directive.Function}({Format(vector, widths)}): the interpreter rejected this input: {ex.Message}");
            return false;
        }
    }

    // Globals of the module are compared after the run, except those holding addresses (which differ between the two).
    var compared = reference.Globals
        .Where(g => g.Initializer is null || !g.Initializer.Items.Any(item => item is DataSymbolRef))
        .ToList();

    ulong[]? actual;
    long cycles = 0;
    byte[][]? globals = null;
    string error;
    try
    {
        actual = RunCompiled(text, directive, vectors, resultBytes, execTarget, emulator, maxTicks, compared, out cycles, out globals, out error);
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

    for (var i = 0; i < compared.Count; i++)
    {
        var address = interpreter.GetGlobalAddress(compared[i].SymbolName);
        var want = interpreter.Memory.AsSpan(address, compared[i].SizeInBytes);
        if (!want.SequenceEqual(globals![i]))
        {
            Console.Error.WriteLine($"@{directive.Function}: global @{compared[i].SymbolName} differs after the run: compiled {Convert.ToHexString(globals[i])}, interpreter {Convert.ToHexString(want)}");
            return false;
        }
    }

    // Cycles of the function alone (the driver's loop and printing are excluded),
    // summed over the vectors: a figure to compare between compiler versions,
    // not an average case.
    Console.WriteLine($"@{directive.Function}: {vectors.Count} vectors match, {cycles} cycles");
    return true;
}

// Compiles the module plus a driver for the function and runs it in the
// emulator; returns one result per vector, or null with an explanation.
static ulong[]? RunCompiled(string text, ExecDirective directive, List<ulong[]> vectors, int resultBytes, ExecTarget execTarget, FileInfo emulator, int maxTicks, List<MirGlobal> compared, out long cycles, out byte[][]? globals, out string error)
{
    var target = execTarget.Create();
    var module = MirModule.Parse(new StringReader(text));
    var functionName = directive.Function;
    var function = module.Functions.First(f => f.Name == functionName);
    KeepReachable(module, function);
    var bufferSymbols = AddBuffers(module, directive);
    DriverBuilder.AddDriver(module, function, vectors, execTarget, bufferSymbols);

    var passManager = new PassManager(null, null);
    PipelineBuilder.AddStandardPipeline(passManager, target, ExecSettings.Tuning);
    passManager.Run(new CompilationContext(module));

    var origin = target.DefaultOrigin!.Value;
    var encoded = target.BuildProgramWithSymbols(module, origin);
    var program = encoded.Bytes;
    var functionAddresses = encoded.FunctionAddresses;
    cycles = 0;
    globals = null;

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
        foreach (var global in compared)
        {
            var address = global.FixedAddress ?? encoded.GlobalAddresses[global.SymbolName];
            startInfo.ArgumentList.Add("--dump");
            startInfo.ArgumentList.Add($"{address:X}:{global.SizeInBytes:X}");
        }
        startInfo.ArgumentList.Add("--measure");
        startInfo.ArgumentList.Add(functionAddresses[functionName].ToString("X4", CultureInfo.InvariantCulture));

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

        var measurement = Regex.Match(stderr.Result, @"cycles (\d+) calls (\d+)");
        if (!measurement.Success)
        {
            error = $"the emulator did not report a cycle count: {stderr.Result.Trim()}";
            return null;
        }
        cycles = long.Parse(measurement.Groups[1].Value, CultureInfo.InvariantCulture);

        var dumps = Regex.Matches(stderr.Result, @"^dump [0-9A-F]{4} ([0-9A-F]*)$", RegexOptions.Multiline);
        if (dumps.Count != compared.Count)
        {
            error = $"the emulator dumped {dumps.Count} of {compared.Count} globals: {stderr.Result.Trim()}";
            return null;
        }
        globals = dumps.Select(m => Convert.FromHexString(m.Groups[1].Value)).ToArray();

        error = string.Empty;
        if (resultBytes == 0)
            return new ulong[vectors.Count];
        return Enumerable.Range(0, vectors.Count)
            .Select(i => ulong.Parse(digits.AsSpan(i * resultBytes * 2, resultBytes * 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            .ToArray();
    }
    finally
    {
        directory.Delete(recursive: true);
    }
}

// Adds a global of seeded bytes for each buf(N) argument of the directive (the same
// bytes on every call, so the interpreter's module and the compiled one agree);
// returns the global each parameter points into, null for the others. Zeros are
// frequent, and the last byte is always zero so a walk to the first zero stops.
static List<string?> AddBuffers(MirModule module, ExecDirective directive)
{
    var symbols = new List<string?>();
    for (var i = 0; i < directive.Arguments.Count; i++)
    {
        if (directive.Arguments[i] is not ArgumentDomain.Buffer buffer)
        {
            symbols.Add(null);
            continue;
        }

        var bytes = new byte[buffer.Size];
        var random = new Random(0xB0F + i);
        for (var b = 0; b < bytes.Length; b++)
            bytes[b] = random.Next(4) == 0 ? (byte)0 : (byte)random.Next(1, 256);
        bytes[^1] = 0;

        var symbol = $"__exec_buf{i}";
        module.Globals.Add(new MirGlobal(symbol, IRType.I8, bytes.Length, new MirInitializer([new DataBytes(bytes)])));
        symbols.Add(symbol);
    }

    return symbols;
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

/// <summary>The command-line choices the local functions above read.</summary>
static class ExecSettings
{
    public static Irie.Target.PlacementTuning? Tuning;
}

static class PlacementTuningCli
{
    /// <summary>--tune is applied over the presets, and --beam-width and --choose-conventions over it.</summary>
    public static Irie.Target.PlacementTuning Resolve(string? tune, bool size, bool speed, int? beamWidth, bool chooseConventions)
    {
        var tuning = size ? Irie.Target.PlacementTuning.Size : speed ? Irie.Target.PlacementTuning.Speed : new Irie.Target.PlacementTuning();
        tuning = Irie.Target.PlacementTuning.Parse(tune, tuning);
        return tuning with { BeamWidth = beamWidth ?? tuning.BeamWidth, ChooseConventions = tuning.ChooseConventions || chooseConventions };
    }
}
