using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

using DotNetro.Compiler;

using Microsoft.CodeAnalysis;

namespace DotNetro.WebPlayground;

/// <summary>
/// The surface the JavaScript shell calls. Structured results cross the boundary as JSON
/// strings (see PlaygroundJson); binary inputs cross as byte arrays.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Playground
{
    private const int MaxCachedCompiles = 16;

    private static readonly Dictionary<(bool Optimize, string Source), string> s_roslynCache = [];

    /// <summary>JSON <c>TargetInfo[]</c>.</summary>
    [JSExport]
    public static string GetTargets()
        => JsonSerializer.Serialize(
            TargetRegistry.Targets.Select(t => new TargetInfo(t.Id, t.DisplayName, t.EmulatorKind)).ToArray(),
            PlaygroundJson.Default.TargetInfoArray);

    /// <summary>
    /// Compiles C# to an IL assembly. Returns JSON <c>{ ok, diagnostics[], assembly? }</c>
    /// where <c>assembly</c> is the base64 DLL. Results are cached per source and options.
    /// </summary>
    [JSExport]
    public static string CompileCSharp(string source, bool optimize)
    {
        if (s_roslynCache.TryGetValue((optimize, source), out var cached))
            return cached;

        var (assembly, diagnostics) = RoslynCompiler.Compile(source, optimize);
        var result = JsonSerializer.Serialize(
            new CSharpResult(
                assembly != null,
                [.. diagnostics
                    .Where(d => d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden)
                    .Select(ToDiagnostic)],
                assembly is null ? null : Convert.ToBase64String(assembly)),
            PlaygroundJson.Default.CSharpResult);

        if (s_roslynCache.Count >= MaxCachedCompiles)
            s_roslynCache.Clear();
        s_roslynCache[(optimize, source)] = result;
        return result;
    }

    /// <summary>
    /// Compiles an IL assembly for a target. Returns JSON
    /// <c>{ ok, diagnostics[], asm?, origin?, image?, passSnapshots? }</c> (<c>image</c> is base64).
    /// </summary>
    [JSExport]
    public static string CompileForTarget(byte[] assembly, string targetId, bool captureSnapshots)
    {
        TargetDescriptor target;
        try
        {
            target = TargetRegistry.Get(targetId);
        }
        catch (ArgumentException ex)
        {
            return Serialize(Failed(ex.Message));
        }

        CompilationResult result;
        try
        {
            result = CompilerDriver.Compile(new CompileRequest(
                assembly, AssemblySet.CreateResolver(target), target.Id, CaptureSnapshots: captureSnapshots));
        }
        catch (Exception ex)
        {
            return Serialize(Failed($"Internal compiler error: {ex.GetType().Name}: {ex.Message}"));
        }

        return Serialize(new TargetResult(
            result.Succeeded,
            [.. result.Diagnostics.Select(d => new Diagnostic(d.Severity.ToString().ToLowerInvariant(), d.Message, null, null, d.Source))],
            result.Succeeded ? result.AssemblyCode : null,
            result.Succeeded ? result.Origin : null,
            result.Succeeded ? Convert.ToBase64String(result.CompiledImage.ToArray()) : null,
            result.PassSnapshots?.Select(s => new PassSnapshotInfo(s.PassName, s.MirText)).ToArray()));

        static TargetResult Failed(string message)
            => new(false, [new Diagnostic("error", message, null, null, "dotnetro")], null, null, null, null);

        static string Serialize(TargetResult r) => JsonSerializer.Serialize(r, PlaygroundJson.Default.TargetResult);
    }

    private static Diagnostic ToDiagnostic(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var position = d.Location.GetLineSpan().StartLinePosition;
        return new Diagnostic(
            d.Severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => "error",
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => "warning",
                _ => "info",
            },
            $"{d.Id}: {d.GetMessage()}",
            d.Location.IsInSource ? position.Line + 1 : null,
            d.Location.IsInSource ? position.Character + 1 : null,
            "roslyn");
    }
}

public sealed record TargetInfo(string Id, string DisplayName, string EmulatorKind);

public sealed record Diagnostic(string Severity, string Message, int? Line, int? Column, string Source);

public sealed record CSharpResult(bool Ok, Diagnostic[] Diagnostics, string? Assembly);

public sealed record PassSnapshotInfo(string PassName, string MirText);

public sealed record TargetResult(
    bool Ok,
    Diagnostic[] Diagnostics,
    string? Asm,
    int? Origin,
    string? Image,
    PassSnapshotInfo[]? PassSnapshots);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TargetInfo[]))]
[JsonSerializable(typeof(CSharpResult))]
[JsonSerializable(typeof(TargetResult))]
internal sealed partial class PlaygroundJson : JsonSerializerContext;
