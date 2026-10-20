using System.CommandLine;
using DotNetro.Compiler;

return Program.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

partial class Program
{
    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        var assemblyOption = new Option<string?>("--assembly") { Description = "Path to the assembly to compile, or - for stdin" };
        var outputOption = new Option<FileInfo?>("--output") { Description = "Path to the output file, or - for stdout" };
        var emitOption = new Option<EmitFormat?>("--emit") { Description = "The format to emit (Assembly, Program, or Executable)", DefaultValueFactory = x => EmitFormat.Executable };

        var targetOption = new Option<string>("--target") { Description = "Target system id (e.g. bbcmicro)", DefaultValueFactory = _ => TargetRegistry.BbcMicroId };

        var statsOption = new Option<bool>("--stats") { Description = "Print what the passes report about the program (zero-page use, …) to stderr" };

        var chooseConventionsOption = new Option<bool>("--choose-conventions")
        {
            Description = "Let the placement of a function that is only called directly choose which of A and X its arguments arrive in and its results leave in (default: every function uses the default calling convention)",
        };

        var tuneOption = new Option<string?>("--tune")
        {
            Description = "Placement search knobs, comma separated: beam=N (0 keeps every state: the oracle), size-weight=N, speed-weight=N, lookahead=N, freq-base=N, choose-conventions",
        };

        var optimizeSizeOption = new Option<bool>("--optimize-size") { Description = "Weigh bytes over cycles in the placement search (size-weight=8, speed-weight=1)" };

        var optimizeSpeedOption = new Option<bool>("--optimize-speed") { Description = "Weigh cycles over bytes in the placement search (size-weight=1, speed-weight=8)" };

        var rootCommand = new RootCommand();

        rootCommand.Options.Add(targetOption);
        rootCommand.Options.Add(assemblyOption);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(emitOption);
        rootCommand.Options.Add(statsOption);
        rootCommand.Options.Add(chooseConventionsOption);
        rootCommand.Options.Add(tuneOption);
        rootCommand.Options.Add(optimizeSizeOption);
        rootCommand.Options.Add(optimizeSpeedOption);

        rootCommand.SetAction(parseResult =>
        {
            var assembly = parseResult.GetValue(assemblyOption);
            var output = parseResult.GetValue(outputOption);

            var createdTempAssembly = false;
            if (assembly == null || assembly == "-")
            {
                // Read all bytes from stdin and write them to a temporary file, then compile that file
                var tempFilePath = Path.GetTempFileName();
                using (var tempFileStream = File.OpenWrite(tempFilePath))
                {
                    stdin.CopyTo(tempFileStream);
                }
                assembly = tempFilePath;
                createdTempAssembly = true;
            }

            var compilationResult = CompilerDriver.Compile(
                Path.GetFullPath(assembly),
                "Main",
                parseResult.GetValue(targetOption)!,
                tuning: PlacementTuningCli.Resolve(parseResult.GetValue(tuneOption), parseResult.GetValue(optimizeSizeOption), parseResult.GetValue(optimizeSpeedOption), null, parseResult.GetValue(chooseConventionsOption)));

            if (parseResult.GetValue(statsOption))
                foreach (var (name, value) in compilationResult.Statistics.OrderBy(s => s.Key))
                    Console.Error.WriteLine($"{name}: {value}");

            var outputStream = (output == null || output.FullName == "-")
                ? stdout
                : File.Create(output.FullName);

            switch (parseResult.GetValue(emitOption))
            {
                case EmitFormat.Assembly:
                    using (var writer = new StreamWriter(outputStream))
                    {
                        writer.Write(compilationResult.AssemblyCode);
                    }
                    break;

                case EmitFormat.Program:
                    using (var writer = new BinaryWriter(outputStream))
                    {
                        writer.Write(compilationResult.CompiledProgram.ToArray());
                    }
                    break;

                case EmitFormat.Executable:
                    using (var writer = new BinaryWriter(outputStream))
                    {
                        writer.Write(compilationResult.CompiledImage.ToArray());
                    }
                    if (outputStream is FileStream)
                    {
                        File.WriteAllText(Path.ChangeExtension(output!.FullName, ".asm"), compilationResult.AssemblyCode);
                        File.WriteAllText(Path.ChangeExtension(output!.FullName, ".lst"), compilationResult.Listing);   
                    }
                    break;

                default:
                    throw new Exception("Emit format must be specified");
            }

            if (createdTempAssembly)
            {
                File.Delete(assembly);
            }
        });

        return rootCommand.Parse(args).InvokeAsync().GetAwaiter().GetResult();
    }


    enum EmitFormat
    {
        Assembly,
        Program,
        Executable,
    }
    static class PlacementTuningCli
    {
        /// <summary>--tune is applied over the presets, and --beam-width and --choose-conventions over it.</summary>
        public static Irie.Target.PlacementTuning Resolve(string? tune, bool size, bool speed, int? beamWidth, bool chooseConventions)
        {
            var tuning = size ? Irie.Target.PlacementTuning.Size : speed ? Irie.Target.PlacementTuning.Speed : new Irie.Target.PlacementTuning();
            tuning = Irie.Target.PlacementTuning.Parse(tune, tuning);
            return tuning with { BeamWidth = beamWidth ?? tuning.BeamWidth, ChooseConventions = tuning.ChooseConventions || chooseConventions };
        }
    }
}
