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

        foreach (var opCode in CollectOpcodes(new MemoryStream(assemblyBytes)))
            Console.WriteLine(opCode);

        return 0;
    }


    // Walk every method body in the assembly and return the distinct ILOpCode
    // enum names that appear, in stable sorted order.
    static IEnumerable<string> CollectOpcodes(Stream assembly)
    {
        var opcodes = new SortedSet<string>(StringComparer.Ordinal);

        using var peReader = new PEReader(assembly, PEStreamOptions.LeaveOpen);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
                continue;

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILReader();

            while (il.RemainingBytes > 0)
            {
                var opCode = ReadOpCode(ref il);
                opcodes.Add(opCode.ToString());
                SkipOperand(ref il, opCode);
            }
        }

        return opcodes;
    }

    static ILOpCode ReadOpCode(ref BlobReader reader)
    {
        var b = reader.ReadByte();
        return b == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)b;
    }

    // Advance the reader past an opcode's inline operand bytes. Covers every
    // ECMA-335 operand kind so the instruction stream stays in sync.
    static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
    {
        switch (opCode)
        {
            // No operand.
            default:
                break;

            // 1-byte operand (ShortInlineVar / ShortInlineI / ShortInlineBrTarget).
            case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s: case ILOpCode.Starg_s:
            case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s: case ILOpCode.Stloc_s:
            case ILOpCode.Ldc_i4_s: case ILOpCode.Unaligned:
            case ILOpCode.Br_s: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue_s:
            case ILOpCode.Beq_s: case ILOpCode.Bge_s: case ILOpCode.Bgt_s:
            case ILOpCode.Ble_s: case ILOpCode.Blt_s: case ILOpCode.Bne_un_s:
            case ILOpCode.Bge_un_s: case ILOpCode.Bgt_un_s: case ILOpCode.Ble_un_s:
            case ILOpCode.Blt_un_s: case ILOpCode.Leave_s:
                reader.ReadByte();
                break;

            // 2-byte operand (InlineVar).
            case ILOpCode.Ldarg: case ILOpCode.Ldarga: case ILOpCode.Starg:
            case ILOpCode.Ldloc: case ILOpCode.Ldloca: case ILOpCode.Stloc:
                reader.ReadInt16();
                break;

            // 4-byte operand (InlineI / ShortInlineR / InlineBrTarget / token kinds).
            case ILOpCode.Ldc_i4: case ILOpCode.Ldc_r4:
            case ILOpCode.Br: case ILOpCode.Brfalse: case ILOpCode.Brtrue:
            case ILOpCode.Beq: case ILOpCode.Bge: case ILOpCode.Bgt:
            case ILOpCode.Ble: case ILOpCode.Blt: case ILOpCode.Bne_un:
            case ILOpCode.Bge_un: case ILOpCode.Bgt_un: case ILOpCode.Ble_un:
            case ILOpCode.Blt_un: case ILOpCode.Leave:
            case ILOpCode.Call: case ILOpCode.Calli: case ILOpCode.Callvirt:
            case ILOpCode.Jmp: case ILOpCode.Newobj: case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
            case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld:
            case ILOpCode.Ldsfld: case ILOpCode.Ldsflda: case ILOpCode.Stsfld:
            case ILOpCode.Cpobj: case ILOpCode.Ldobj: case ILOpCode.Castclass:
            case ILOpCode.Isinst: case ILOpCode.Unbox: case ILOpCode.Unbox_any:
            case ILOpCode.Stobj: case ILOpCode.Box: case ILOpCode.Newarr:
            case ILOpCode.Ldelema: case ILOpCode.Ldelem: case ILOpCode.Stelem:
            case ILOpCode.Refanyval: case ILOpCode.Mkrefany: case ILOpCode.Initobj:
            case ILOpCode.Constrained: case ILOpCode.Sizeof: case ILOpCode.Ldtoken:
            case ILOpCode.Ldstr:
                reader.ReadInt32();
                break;

            // 8-byte operand (InlineI8 / InlineR).
            case ILOpCode.Ldc_i8: case ILOpCode.Ldc_r8:
                reader.ReadInt64();
                break;

            // InlineSwitch: 4-byte count followed by count 4-byte targets.
            case ILOpCode.Switch:
                var n = reader.ReadUInt32();
                for (var i = 0; i < n; i++) reader.ReadInt32();
                break;
        }
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
