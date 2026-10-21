// Compiles a C# file to an assembly using Roslyn APIs.
//
// Default mode: writes the emitted assembly bytes to stdout.
//
//   DotNetro.Compiler.Tests.CsCompiler <file.cs>
//
// --opcodes mode: instead of the assembly bytes, walks the IL of every method
// body in the compiled assembly and writes the distinct ILOpCode enum names it
// uses to stdout, one per line. Consumed by DotNetro.Tools.ILSupport to work
// out which IL opcodes are exercised by the lit-test corpus.
//
//   DotNetro.Compiler.Tests.CsCompiler --opcodes <file.cs>
//
// --optimize: compile with Roslyn's Release optimisation level instead of the
// default Debug one. Release IL has no Debug nops/temporaries and uses the fused
// compare-and-branch opcodes (beq, bge, blt, ...) and `br` / `brtrue` forms that
// Debug IL does not. Combine with either mode above.

using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

return Program.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

partial class Program
{
    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        var emitOpcodes = false;
        var optimize = false;
        string? csFilePath = null;
        foreach (var arg in args)
        {
            if (arg == "--opcodes")
                emitOpcodes = true;
            else if (arg == "--optimize")
                optimize = true;
            else
                csFilePath = arg;
        }

        if (csFilePath is null)
        {
            Console.Error.WriteLine("usage: DotNetro.Compiler.Tests.CsCompiler [--opcodes] [--optimize] <file.cs>");
            return 1;
        }

        var source = File.ReadAllText(csFilePath);
        var assemblyName = Path.GetFileNameWithoutExtension(csFilePath);

        var assemblyBytes = CompilationCache.GetOrCompile(source, assemblyName, optimize, out var diagnostics);
        if (assemblyBytes is null)
        {
            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic);
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
    /// Compiles the lit test sources. A lit test compiles the same file once per RUN line, and the
    /// tool runs in the test process, so the result is kept (and the metadata references loaded once).
    /// </summary>
    static class CompilationCache
    {
        private static readonly ConcurrentDictionary<(string Source, string AssemblyName, bool Optimize), byte[]> Assemblies = new();

        private static readonly SyntaxTree GlobalUsings = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

        private static readonly MetadataReference[] References = CreateReferences();

        private static MetadataReference[] CreateReferences()
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            return
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
            ];
        }

        /// <summary>Returns the emitted assembly, or null with the compiler's diagnostics.</summary>
        public static byte[]? GetOrCompile(string source, string assemblyName, bool optimize, out IReadOnlyList<Diagnostic> diagnostics)
        {
            diagnostics = [];
            if (Assemblies.TryGetValue((source, assemblyName, optimize), out var cached))
                return cached;

            var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: [GlobalUsings, syntaxTree],
                references: References,
                options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                    .WithOptimizationLevel(optimize ? OptimizationLevel.Release : OptimizationLevel.Debug)
                    .WithAllowUnsafe(true));

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                diagnostics = emitResult.Diagnostics;
                return null;
            }

            return Assemblies[(source, assemblyName, optimize)] = ms.ToArray();
        }
    }
}
