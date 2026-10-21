// Assembles an IL (.il) file to an assembly using Microsoft's ilasm.
//
// Default mode: writes the assembled assembly bytes to stdout.
//
//   DotNetro.Compiler.Tests.ILCompiler <file.il>
//
// --opcodes mode: instead of the assembly bytes, writes the distinct ILOpCode enum names
// the assembly uses, one per line (see DotNetro.Tools.ILSupport).
//
//   DotNetro.Compiler.Tests.ILCompiler --opcodes <file.il>
//
// For IL that C# cannot produce (tail., jmp, unbox, ...). The test file is plain ilasm
// syntax; `//` comments carry the lit RUN/CHECK directives.

using System.Collections.Concurrent;
using System.Diagnostics;

return Program.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

partial class Program
{
    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        var emitOpcodes = false;
        string? ilFilePath = null;
        foreach (var arg in args)
        {
            if (arg == "--opcodes")
                emitOpcodes = true;
            else
                ilFilePath = arg;
        }

        if (ilFilePath is null)
        {
            Console.Error.WriteLine("usage: DotNetro.Compiler.Tests.ILCompiler [--opcodes] <file.il>");
            return 1;
        }

        var assemblyBytes = AssemblyCache.GetOrAssemble(ilFilePath, out var errors);
        if (assemblyBytes is null)
        {
            Console.Error.WriteLine(errors);
            return 1;
        }

        if (!emitOpcodes)
        {
            stdout.Write(assemblyBytes);
            return 0;
        }

        foreach (var opCode in OpcodeScanner.CollectOpcodes(new MemoryStream(assemblyBytes)))
            Console.WriteLine(opCode);

        return 0;
    }

    /// <summary>
    /// Assembles the lit test sources. A lit test assembles the same file once per RUN line, and
    /// the tool runs in the test process, so the result is kept.
    /// </summary>
    static class AssemblyCache
    {
        private sealed record Result(byte[]? Assembly, string Errors);

        private static readonly ConcurrentDictionary<string, Lazy<Result>> Results = new();

        public static byte[]? GetOrAssemble(string ilFilePath, out string errors)
        {
            var source = File.ReadAllText(ilFilePath);
            var result = Results.GetOrAdd(source, _ => new Lazy<Result>(() => Assemble(ilFilePath))).Value;
            errors = result.Errors;
            return result.Assembly;
        }

        private static Result Assemble(string ilFilePath)
        {
            var ilasm = FindILAsm();
            var output = Path.Combine(Path.GetTempPath(), $"ilcompiler-{Guid.NewGuid():N}.exe");
            try
            {
                var startInfo = new ProcessStartInfo(ilasm)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("-nologo");
                startInfo.ArgumentList.Add("-quiet");
                startInfo.ArgumentList.Add("-exe");
                startInfo.ArgumentList.Add($"-output={output}");
                startInfo.ArgumentList.Add(ilFilePath);

                using var process = Process.Start(startInfo)!;
                // ilasm reports errors on stdout.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                var text = stdoutTask.Result + stderrTask.Result;

                return process.ExitCode == 0 && File.Exists(output)
                    ? new Result(File.ReadAllBytes(output), text)
                    : new Result(null, text);
            }
            finally
            {
                File.Delete(output);
            }
        }

        /// <summary>The ilasm from the RID-specific package, which restore copies to runtimes/&lt;rid&gt;/native.</summary>
        private static string FindILAsm()
        {
            var fileName = OperatingSystem.IsWindows() ? "ilasm.exe" : "ilasm";
            // Not AppContext.BaseDirectory: in a lit test the tool runs inside the test process.
            var runtimes = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "runtimes");
            var found = Directory.EnumerateFiles(runtimes, fileName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"{fileName} not found under {runtimes} (the ILAsm package for this RID).");

            // NuGet does not keep the executable bit.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(found, File.GetUnixFileMode(found) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return found;
        }
    }
}
