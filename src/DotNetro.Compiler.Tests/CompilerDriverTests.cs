using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetro.Compiler.Tests;

public sealed class CompilerDriverTests
{
    private const string HelloWorld = """Console.WriteLine("Hello, World!");""";

    [Test]
    public async Task InMemoryCompileMatchesPathBasedCompile()
    {
        var dll = CompileCSharp(HelloWorld);

        var path = Path.Combine(Path.GetTempPath(), $"dnrc-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, dll);
        CompilationResult pathResult;
        try
        {
            pathResult = CompilerDriver.Compile(path, "Main");
        }
        finally
        {
            File.Delete(path);
        }

        var result = CompilerDriver.Compile(new CompileRequest(dll, CreateInMemoryResolver(), TargetRegistry.BbcMicroId));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.AssemblyCode).IsNotEmpty();
        await Assert.That(result.AssemblyCode).IsEqualTo(pathResult.AssemblyCode);
        await Assert.That(result.CompiledProgram.ToArray()).IsEquivalentTo(pathResult.CompiledProgram.ToArray());
        await Assert.That(result.CompiledImage.ToArray()).IsEquivalentTo(pathResult.CompiledImage.ToArray());
        await Assert.That(result.Origin).IsEqualTo(0x2000);
        await Assert.That(result.PassSnapshots).IsNull();
    }

    // Every framework reference (System.Runtime, System.Console, ...) is redirected to
    // DotNetro.CoreLib before the resolver is asked, so the only assembly the compiler
    // ever requests is the core library: the host's CoreLib and facades are unreachable.
    [Test]
    public async Task OnlyTheDotNetroCoreLibraryIsRequestedFromTheResolver()
    {
        var dll = CompileCSharp(HelloWorld);
        var resolver = new RecordingResolver(CreateInMemoryResolver());

        var result = CompilerDriver.Compile(new CompileRequest(dll, resolver, TargetRegistry.BbcMicroId));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(resolver.Requested.Distinct()).IsEquivalentTo([WellKnownAssemblies.CoreLib]);
    }

    [Test]
    public async Task UnsupportedConstructIsReportedAsDiagnostic()
    {
        var dll = CompileCSharp("""
            double x = 1.5;
            Console.WriteLine((int)(x * 2));
            """);

        var result = CompilerDriver.Compile(new CompileRequest(dll, CreateInMemoryResolver(), TargetRegistry.BbcMicroId));

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Diagnostics).Count().IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(result.Diagnostics[0].Source).IsEqualTo("dotnetro");
        await Assert.That(result.Diagnostics[0].Message).Contains("not supported");
        await Assert.That(result.CompiledImage).IsEmpty();
    }

    [Test]
    public async Task CaptureSnapshotsRecordsMirAfterEachPass()
    {
        var dll = CompileCSharp(HelloWorld);

        var result = CompilerDriver.Compile(new CompileRequest(dll, CreateInMemoryResolver(), TargetRegistry.BbcMicroId, CaptureSnapshots: true));

        await Assert.That(result.PassSnapshots).IsNotNull();
        await Assert.That(result.PassSnapshots!).IsNotEmpty();
        await Assert.That(result.PassSnapshots!.All(s => s.PassName.Length > 0 && s.MirText.Length > 0)).IsTrue();
    }

    [Test]
    public async Task UnknownTargetIsRejected()
    {
        var dll = CompileCSharp(HelloWorld);

        await Assert.That(() => CompilerDriver.Compile(new CompileRequest(dll, CreateInMemoryResolver(), "zx80")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BbcMicroTargetIsRegistered()
    {
        var target = TargetRegistry.Get(TargetRegistry.BbcMicroId);

        await Assert.That(target.DisplayName).IsEqualTo("BBC Micro");
        await Assert.That(target.EmulatorKind).IsEqualTo("jsbeeb");
    }

    // The core library supplied as bytes (the DotNetro.CoreLib.dll copied beside the
    // tests), the way the playground supplies it from an embedded file.
    private static InMemoryAssemblyResolver CreateInMemoryResolver()
        => new InMemoryAssemblyResolver().Add(
            WellKnownAssemblies.CoreLib,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, WellKnownAssemblies.CoreLib + ".dll")));

    private sealed class RecordingResolver(IAssemblyResolver inner) : IAssemblyResolver
    {
        public List<string> Requested { get; } = [];

        public byte[]? Resolve(System.Reflection.AssemblyName assemblyName)
        {
            Requested.Add(assemblyName.Name ?? assemblyName.FullName);
            return inner.Resolve(assemblyName);
        }
    }

    // Mirrors DotNetro.Compiler.Tests.CsCompiler (Release IL, top-level statements).
    private static byte[] CompileCSharp(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var globalUsings = CSharpSyntaxTree.ParseText("global using System;", parseOptions);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var compilation = CSharpCompilation.Create(
            "InMemoryProgram",
            [globalUsings, CSharpSyntaxTree.ParseText(source, parseOptions)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(true));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics));
        return stream.ToArray();
    }
}
