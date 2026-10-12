using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetro.WebPlayground;

/// <summary>Compiles C# source to an IL assembly, in memory.</summary>
internal static class RoslynCompiler
{
    private static readonly CSharpParseOptions s_parseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    // Mirrors DotNetro.Compiler.Tests.CsCompiler.
    private static readonly SyntaxTree s_globalUsings = CSharpSyntaxTree.ParseText("""
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Threading;
        global using System.Threading.Tasks;
        """, s_parseOptions);

    private static readonly MetadataReference[] s_references =
        [.. AssemblySet.RoslynReferences.Select(image => MetadataReference.CreateFromImage(image))];

    public static (byte[]? Assembly, List<Microsoft.CodeAnalysis.Diagnostic> Diagnostics) Compile(string source, bool optimize)
    {
        var compilation = CSharpCompilation.Create(
            "Program",
            [s_globalUsings, CSharpSyntaxTree.ParseText(source, s_parseOptions)],
            s_references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(optimize ? OptimizationLevel.Release : OptimizationLevel.Debug)
                .WithAllowUnsafe(true)
                // WebAssembly (single-threaded) has no worker threads for Roslyn to fan out to.
                .WithConcurrentBuild(false));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        return (emitResult.Success ? stream.ToArray() : null, [.. emitResult.Diagnostics]);
    }
}
