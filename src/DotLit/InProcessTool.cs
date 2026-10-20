using System.Collections.Concurrent;
using System.Reflection;

namespace DotLit;

/// <summary>
/// Runs a tool the lit tests would otherwise launch as a process (<c>iriec</c>, <c>dnrc</c>, the
/// emulator, …) inside the current process, by loading its assembly and invoking its entry point
/// with the console bound by <see cref="ToolConsole"/>. A process pays runtime start-up and JIT on
/// every launch; here that is paid once per test run.
/// </summary>
/// <remarks>
/// The tools do not reference DotLit. A tool is runnable here when its entry-point class has a
/// <c>static int Run(string[] args, Stream stdin, Stream stdout)</c> (its <c>Main</c> calls it with the
/// process's streams), writes text through <see cref="Console"/> (routed per run by <see cref="ToolConsole"/>)
/// and returns an exit code instead of calling <see cref="Environment.Exit(int)"/>. The runner is also
/// published as the <c>"DotLit.RunTool"</c> <see cref="AppContext"/> value, a
/// <c>Func&lt;string, IReadOnlyList&lt;string&gt;, Stream, Stream, Stream, int&gt;</c> (tool path, arguments, stdin,
/// stdout, stderr), for tools that run other tools.
/// </remarks>
public static class InProcessTool
{
    private static readonly ConcurrentDictionary<string, MethodInfo?> EntryPoints = new();

    static InProcessTool() =>
        AppContext.SetData("DotLit.RunTool", new Func<string, IReadOnlyList<string>, Stream, Stream, Stream, int>(Run));

    /// <summary>The exit code reported when the tool throws, as the runtime does for an unhandled exception.</summary>
    private const int UnhandledExceptionExitCode = 134;

    /// <summary>Whether <paramref name="toolPath"/> (an executable path without extension) has a managed assembly beside it that can be run in-process.</summary>
    public static bool CanRun(string toolPath) => EntryPoint(toolPath) is not null;

    /// <summary>Runs the tool to completion and returns its exit code.</summary>
    public static int Run(string toolPath, IReadOnlyList<string> args, Stream stdin, Stream stdout, Stream stderr)
    {
        var run = EntryPoint(toolPath) ?? throw new FileNotFoundException($"No runnable tool assembly at '{toolPath}.dll'.");
        return ToolConsole.Run(stdin, stdout, stderr, () => Invoke(run, args));
    }

    private static int Invoke(MethodInfo run, IReadOnlyList<string> args)
    {
        try
        {
            return (int)run.Invoke(null, [args.ToArray(), ToolConsole.OpenStandardInput(), ToolConsole.OpenStandardOutput()])!;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unhandled exception. " + (exception is TargetInvocationException { InnerException: { } inner } ? inner : exception));
            return UnhandledExceptionExitCode;
        }
    }

    private static MethodInfo? EntryPoint(string toolPath) =>
        EntryPoints.GetOrAdd(Path.GetFullPath(toolPath), static path =>
        {
            var assemblyPath = path + ".dll";
            return File.Exists(assemblyPath)
                ? Assembly.LoadFrom(assemblyPath).EntryPoint?.DeclaringType?.GetMethod(
                    "Run", BindingFlags.Public | BindingFlags.Static, [typeof(string[]), typeof(Stream), typeof(Stream)])
                : null;
        });
}
