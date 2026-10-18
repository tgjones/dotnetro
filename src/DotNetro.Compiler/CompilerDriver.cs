using System.Collections.ObjectModel;

using Irie.Mir;
using Irie.Passes;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    // File-based entry point used by dnrc and the tests: compiles for the given
    // target (default the BBC Micro), resolving the core library and the DotNetro
    // runtime through the host runtime. Failures, including an unknown target id,
    // surface as exceptions.
    public static CompilationResult Compile(
        string dotNetAssemblyPath, string entryPointMethodName, string targetId = TargetRegistry.BbcMicroId,
        PlacementStrategy placement = PlacementStrategy.Colouring)
        => CompileCore(new CompileRequest(
            File.ReadAllBytes(dotNetAssemblyPath),
            HostAssemblyResolver.ForTarget(targetId),
            targetId,
            entryPointMethodName)
        {
            Placement = placement,
        });

    // Byte-based entry point: nothing here touches the file system. Expected
    // failures (the program uses something the compiler does not support) are
    // reported as Diagnostics on the result rather than thrown; anything else is a
    // compiler bug and still throws.
    public static CompilationResult Compile(CompileRequest request)
    {
        try
        {
            return CompileCore(request);
        }
        catch (NotSupportedException ex)
        {
            return CompilationResult.Failure([new CompileDiagnostic(DiagnosticSeverity.Error, ex.Message, "dotnetro")]);
        }
    }

    // IL→MIR pipeline: translate the IL to an Irie MirModule, run it through
    // the shared Irie pass pipeline (same as iriec), and produce program (raw) +
    // image bytes.
    private static CompilationResult CompileCore(CompileRequest request)
    {
        var descriptor = TargetRegistry.Get(request.TargetId);
        var target = descriptor.CreateTarget();

        MirModule module;
        using (var translator = new IlToMirTranslator(request.Assembly, request.Resolver, target.GetRuntime()))
        {
            module = translator.Translate(request.EntryPoint);
        }

        // Developer aid: DNRC_DUMP_MIR=<path> writes the translated module as MIR
        // text before the pass pipeline runs, so a failing function can be pulled
        // out and replayed through `iriec --stop-after <pass>`.
        if (Environment.GetEnvironmentVariable("DNRC_DUMP_MIR") is { Length: > 0 } dumpPath)
        {
            using var dump = new StreamWriter(dumpPath);
            module.Write(dump, target.RegisterInfo.GetRegisterName);
        }

        var context = new CompilationContext(module);

        var passMgr = new PassManager(null, null);
        PipelineBuilder.AddStandardPipeline(passMgr, target, request.Placement);

        List<PassSnapshot>? snapshots = null;
        if (request.CaptureSnapshots)
        {
            snapshots = [];
            string WriteModule()
            {
                var writer = new StringWriter();
                module.Write(writer, target.RegisterInfo.GetRegisterName);
                return writer.ToString();
            }

            var previous = WriteModule();
            snapshots.Add(new PassSnapshot("Translation", previous));
            passMgr.AfterEachPass = (pass, _) =>
            {
                var text = WriteModule();
                snapshots.Add(new PassSnapshot(pass.Name, text == previous ? null : text));
                previous = text;
            };
        }

        passMgr.Run(context);

        var origin = target.DefaultOrigin!.Value;
        var machineCode = target.EmitProgram(module);
        var program = target.Encode(machineCode, origin);
        var image = target.PackageImage(program, origin);

        var machineCodeWriter = new StringWriter();
        descriptor.WriteAssembly(machineCode, machineCodeWriter);

        // The MIR pipeline does not produce an assembly-text listing; the
        // Listing field stays empty.
        return new CompilationResult(
            machineCodeWriter.ToString(),
            string.Empty,
            new ReadOnlyCollection<byte>(program),
            new ReadOnlyCollection<byte>(image))
        {
            Origin = origin,
            PassSnapshots = snapshots,
        };
    }
}

/// <summary>Input to <see cref="CompilerDriver.Compile(CompileRequest)"/>.</summary>
/// <param name="Assembly">The PE image of the program to compile.</param>
/// <param name="Resolver">Supplies every other assembly the compiler needs. Framework references in the program are redirected to <see cref="WellKnownAssemblies.CoreLib"/>, so that is all it has to supply besides third-party libraries.</param>
/// <param name="TargetId">A <see cref="TargetRegistry"/> id.</param>
/// <param name="EntryPoint">Name of the entry point method (falls back to the top-level-statements <c>&lt;Main&gt;$</c>).</param>
/// <param name="CaptureSnapshots">Capture the module's MIR before the first pass and after each pass into <see cref="CompilationResult.PassSnapshots"/>.</param>
public sealed record CompileRequest(
    byte[] Assembly,
    IAssemblyResolver Resolver,
    string TargetId,
    string EntryPoint = "Main",
    bool CaptureSnapshots = false)
{
    /// <summary>How the Irie pipeline places values in registers and memory.</summary>
    public PlacementStrategy Placement { get; init; } = PlacementStrategy.Colouring;
}

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record CompileDiagnostic(DiagnosticSeverity Severity, string Message, string Source);

/// <summary>
/// The module's MIR text after one pass of the pipeline. <paramref name="MirText"/> is <c>null</c> when
/// it is identical to the previous snapshot's (pass names are not unique; identify a snapshot by its index).
/// </summary>
public sealed record PassSnapshot(string PassName, string? MirText);

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage)
{
    /// <summary>Load address of <see cref="CompiledProgram"/> (and of the image's program).</summary>
    public int Origin { get; init; }

    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Set only when <see cref="CompileRequest.CaptureSnapshots"/> was requested. The first entry is the
    /// <c>Translation</c> baseline (before any pass) and always has text; a later entry's text is
    /// <c>null</c> when the pass left the module unchanged.
    /// </summary>
    public IReadOnlyList<PassSnapshot>? PassSnapshots { get; init; }

    public bool Succeeded => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    internal static CompilationResult Failure(IReadOnlyList<CompileDiagnostic> diagnostics)
        => new(string.Empty, string.Empty, new ReadOnlyCollection<byte>([]), new ReadOnlyCollection<byte>([]))
        {
            Diagnostics = diagnostics,
        };
}
