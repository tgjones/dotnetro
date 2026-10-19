using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// generate-benchmarks: the C#-derived benchmark set.
/// </summary>
/// <remarks>
/// Each program under src/DotNetro.Compiler.Tests/Lit/Benchmarks/ is also an ordinary
/// lit test (dotnet vs emulator output must match), so the code being measured is
/// known to be correct. The measured code is every method of the program's `Bench`
/// class; the scoreboard reports Irie's instruction count and code bytes for each
/// kernel. There is no reference compiler here, so the numbers are a regression
/// baseline for Irie against itself, aimed at the C# workload (i32 arithmetic,
/// runtime-helper calls, pointer/field/element access) that the llvm-mos C corpus
/// under-represents.
/// </remarks>
static class BenchmarkReport
{
    /// <summary>Methods of the `Bench` class mangle to labels starting with this prefix.</summary>
    const string KernelPrefix = "Bench_";

    public static Command Build()
    {
        var dirOption = new Option<string?>("--benchmarks") { Description = "Directory of benchmark .cs programs (default: src/DotNetro.Compiler.Tests/Lit/Benchmarks)." };
        var outOption = new Option<string?>("--out") { Description = "Output markdown path (default: <repo>/doc/dotnetro/benchmarks.md)." };

        var cmd = new Command("generate-benchmarks", "Score Irie's code size on the C#-derived benchmark set.");
        cmd.Options.Add(dirOption);
        cmd.Options.Add(outOption);
        cmd.SetAction(parse =>
        {
            var (srcDir, repoRoot) = LocateRepo();
            var dir = parse.GetValue(dirOption) ?? Path.Combine(srcDir, "DotNetro.Compiler.Tests", "Lit", "Benchmarks");
            var outPath = parse.GetValue(outOption) ?? Path.Combine(repoRoot, "doc", "dotnetro", "benchmarks.md");
            var csCompiler = LocateTool(srcDir, "DotNetro.Compiler.Tests.CsCompiler");
            var dnrc = LocateTool(srcDir, "DotNetro.Compiler.Driver", "dnrc");
            foreach (var tool in new[] { csCompiler, dnrc })
                if (!File.Exists(tool)) { Console.Error.WriteLine($"tool not found: {tool} (build the solution first)"); return 1; }

            var rows = new List<Row>();
            var failed = false;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs").OrderBy(p => p))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dll = Run(csCompiler, null, "--optimize", file);
                if (dll.ExitCode != 0) { Console.Error.WriteLine($"{name}: C# compile failed:\n{dll.Error}"); failed = true; continue; }
                var asm = Run(dnrc, dll.Output, "--emit", "assembly");
                if (asm.ExitCode != 0) { Console.Error.WriteLine($"{name}: dnrc failed:\n{asm.Error}"); failed = true; continue; }
                foreach (var f in SplitFunctions(asm.Text).Where(f => f.Name.StartsWith(KernelPrefix, StringComparison.Ordinal)))
                    rows.Add(new Row(name, f.Name[KernelPrefix.Length..], f.Instructions, f.Bytes));
            }
            if (failed) return 1;

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, Render(rows));
            Console.WriteLine($"Wrote {outPath}");
            Console.WriteLine($"Benchmarks: {rows.Select(r => r.Program).Distinct().Count()} programs, {rows.Count} kernels, {rows.Sum(r => r.Instructions)} instructions, {rows.Sum(r => r.Bytes)} code bytes.");
            return 0;
        });
        return cmd;
    }

    record Row(string Program, string Kernel, int Instructions, int Bytes);
    record Function(string Name, int Instructions, int Bytes);

    /// <summary>
    /// dnrc --emit assembly: functions start at a column-0 label that does not begin
    /// with '.'; local labels are '.bbN:'; instructions are indented.
    /// </summary>
    static IEnumerable<Function> SplitFunctions(string asm)
    {
        string? name = null;
        int count = 0, bytes = 0;
        foreach (var raw in asm.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && raw.EndsWith(':') && raw[0] != '.')
            {
                if (name is not null) yield return new Function(name, count, bytes);
                name = raw[..^1];
                count = bytes = 0;
                continue;
            }
            var line = raw.Trim();
            if (line.Length == 0 || line.EndsWith(':') || line.StartsWith('.') || line.StartsWith(';')) continue;
            count++;
            bytes += InstructionSize(line);
        }
        if (name is not null) yield return new Function(name, count, bytes);
    }

    static readonly HashSet<string> Branches = ["BCC", "BCS", "BEQ", "BMI", "BNE", "BPL", "BVC", "BVS"];

    /// <summary>
    /// Encoded size from the mnemonic and operand form. Symbols are absolute
    /// addresses; `$hh` is zero page, `$hhhh` absolute.
    /// </summary>
    static int InstructionSize(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mnemonic = parts[0];
        if (parts.Length == 1) return 1;
        var operand = parts[1].Trim();
        if (Branches.Contains(mnemonic)) return 2;
        if (mnemonic is "JMP" or "JSR") return 3;
        if (operand.StartsWith('#') || operand.StartsWith('(')) return 2;
        var m = Regex.Match(operand, @"^\$([0-9A-Fa-f]+)");
        return m.Success && m.Groups[1].Length <= 2 ? 2 : 3;
    }

    static string Render(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("# C#-derived benchmark set\n\n");
        sb.Append("Generated by `irie-report generate-benchmarks` — do not edit; regenerate after codegen changes and commit the diff.\n\n");
        sb.Append("Each program in [`src/DotNetro.Compiler.Tests/Lit/Benchmarks/`](../../src/DotNetro.Compiler.Tests/Lit/Benchmarks) is also a lit test (host .NET output must equal the emulated output), compiled with Roslyn `--optimize` and `dnrc` for the BBC Micro. ");
        sb.Append("Only the methods of each program's `Bench` class are counted; runtime helpers they call (`__mulsi3`, `__divsi3`, `ManagedHeap`, …) are not. ");
        sb.Append("There is no reference compiler, so these numbers are a baseline for tracking Irie against itself on the C# workload. Bytes are derived from the emitted assembly's addressing modes.\n\n");
        sb.Append($"**Total: {rows.Sum(r => r.Instructions)} instructions, {rows.Sum(r => r.Bytes)} bytes** across {rows.Count} kernels.\n\n");
        sb.Append("| Program | Kernel | Instructions | Bytes |\n|---|---|--:|--:|\n");
        foreach (var r in rows)
            sb.Append($"| {r.Program} | `{r.Kernel}` | {r.Instructions} | {r.Bytes} |\n");
        return sb.ToString();
    }

    record Result(int ExitCode, byte[] Output, string Error)
    {
        public string Text => Encoding.UTF8.GetString(Output);
    }

    /// <summary>Runs `exe args`, feeding `stdin` bytes if given; returns raw stdout bytes.</summary>
    static Result Run(string exe, byte[]? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
        if (stdin is not null)
        {
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.Close();
        }
        outTask.Wait();
        p.WaitForExit();
        return new Result(p.ExitCode, ms.ToArray(), err.Result);
    }

    static (string srcDir, string repoRoot) LocateRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotNetro.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate src/ (DotNetro.slnx) above the running assembly.");
        return (dir.FullName, dir.Parent!.FullName);
    }

    /// <summary>Sibling tools are built with the same config/tfm as this one.</summary>
    static string LocateTool(string srcDir, string project, string? exeName = null)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var exe = (exeName ?? project) + (OperatingSystem.IsWindows() ? ".exe" : "");
        return Path.Combine(srcDir, project, "bin", config, tfm, exe);
    }
}
