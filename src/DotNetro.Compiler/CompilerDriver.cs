using System.Collections.ObjectModel;

using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace DotNetro.Compiler;

public static class CompilerDriver
{
    // IL→MIR pipeline: translate the IL to an Irie MirModule, run it through
    // the shared Irie pass pipeline (same as iriec), and produce program (raw) +
    // image (.ssd) bytes.
    public static CompilationResult Compile(string dotNetAssemblyPath, string entryPointMethodName)
    {
        var target = new MOS6502BbcMicroTarget();

        MirModule module;
        using (var translator = new IlToMirTranslator(dotNetAssemblyPath, target.GetRuntime()))
        {
            module = translator.Translate(entryPointMethodName);
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
        PipelineBuilder.AddStandardPipeline(passMgr, target);
        passMgr.Run(context);

        var origin = target.DefaultOrigin!.Value;
        var machineCode = target.MachineCodeEmitter.Emit(module);
        var program = new MOS6502BinaryEncoder().Encode(machineCode, origin);
        var image = target.PackageImage(program, origin);

        var machineCodeText = "";
        if (machineCode != null)
        {
            var machineCodeWriter = new StringWriter();
            MOS6502AssemblyWriter.Write(machineCode, machineCodeWriter);
            machineCodeText = machineCodeWriter.ToString();
        }

        // The MIR pipeline does not produce an assembly-text listing; the
        // AssemblyCode / Listing fields stay empty (the --emit assembly path
        // produces no output).
        return new CompilationResult(
            machineCodeText,
            string.Empty,
            new ReadOnlyCollection<byte>(program),
            new ReadOnlyCollection<byte>(image));
    }
}

public sealed record CompilationResult(string AssemblyCode, string Listing, ReadOnlyCollection<byte> CompiledProgram, ReadOnlyCollection<byte> CompiledImage);
