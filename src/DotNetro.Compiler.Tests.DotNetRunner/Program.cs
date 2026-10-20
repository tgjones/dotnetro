// Reads a compiled .dll from stdin and runs it under the host .NET runtime,
// feeding optional console input.

using System.CommandLine;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using DotLit;

return Program.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

partial class Program
{
    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        var inputOption = new Option<string?>("--input") { Description = "Console input. Use \\r to separate lines." };

        var rootCommand = new RootCommand("DotNetro .NET runner for Lit tests");
        rootCommand.Options.Add(inputOption);

        rootCommand.SetAction(parseResult =>
        {
            var inputString = parseResult.GetValue(inputOption);

            using var assemblyStream = new MemoryStream();
            stdin.CopyTo(assemblyStream);
            assemblyStream.Position = 0;

            // Run the program in-process, in a context of its own so it can be unloaded afterwards.
            var context = new AssemblyLoadContext("program", isCollectible: true);
            try
            {
                var entryPoint = context.LoadFromStream(assemblyStream).EntryPoint
                    ?? throw new InvalidOperationException("The assembly has no entry point.");

                // Replace the \r escape with a newline so ReadLine() in the program
                // receives separate lines, matching the emulator's --input convention.
                var programInput = new MemoryStream(Encoding.UTF8.GetBytes(inputString?.Replace("\\r", "\n") ?? ""));

                return ToolConsole.Run(programInput, ToolConsole.OpenStandardOutput(), ToolConsole.OpenStandardError(), () =>
                {
                    try
                    {
                        var result = entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? [] : [Array.Empty<string>()]);
                        if (result is Task task)
                            task.GetAwaiter().GetResult();
                        return result is int exitCode ? exitCode : result is Task<int> t ? t.Result : 0;
                    }
                    catch (TargetInvocationException exception)
                    {
                        Console.Error.WriteLine($"Unhandled exception. {exception.InnerException}");
                        return 134;
                    }
                });
            }
            finally
            {
                context.Unload();
            }
        });

        return rootCommand.Parse(args).InvokeAsync().GetAwaiter().GetResult();
    }
}
