using System.CommandLine;
using Irie.Mir;

return Program.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

partial class Program
{
    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        var inputArgument = new Argument<string?>("input") { Description = "Path to the input file, or - for stdin", Arity = ArgumentArity.ZeroOrOne };
        var outputOption = new Option<FileInfo?>("-o") { Description = "Path to the output file, or - for stdout" };

        var rootCommand = new RootCommand("Irie disassembler");
        rootCommand.Arguments.Add(inputArgument);
        rootCommand.Options.Add(outputOption);

        rootCommand.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument);
            var output = parseResult.GetValue(outputOption);

            var inputStream = (input == null || input == "-")
                ? stdin
                : File.OpenRead(input);

            MirModule module;
            using (inputStream != stdin ? inputStream : null)
            using (var binaryReader = new BinaryReader(inputStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                module = MirModule.Read(binaryReader);
            }

            var outputWriter = (output == null || output.FullName == "-")
                ? Console.Out
                : new StreamWriter(output.OpenWrite());

            using (outputWriter != Console.Out ? outputWriter : null)
            {
                module.Write(outputWriter);
            }
        });

        return rootCommand.Parse(args).InvokeAsync().GetAwaiter().GetResult();
    }
}
