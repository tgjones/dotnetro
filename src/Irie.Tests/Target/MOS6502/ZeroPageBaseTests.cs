using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

public sealed class ZeroPageBaseTests
{
    // A 32-bit add takes its second operand in RC4..RC7 (the zero-page registers).
    private const string Add32 = """
        func @f : (i32, i32) -> i32 {
        bb0(%0 : i32, %1 : i32):
            %2 : i32 = arith.addi %0, %1
            core.return %2
        }
        """;

    private sealed class ZeroPageBase2Target : MOS6502Target
    {
        public override int ZeroPageBase => 2;
    }

    private static string Compile(Irie.Target.Target target)
    {
        var module = MirModule.Parse(new StringReader(Add32));
        var passMgr = new PassManager(null, null);
        PipelineBuilder.AddStandardPipeline(passMgr, target);
        passMgr.Run(new CompilationContext(module));

        var writer = new StringWriter();
        MOS6502AssemblyWriter.Write(target.MachineCodeEmitter.Emit(module), writer);
        return writer.ToString();
    }

    [Test]
    public async Task BaseChipPlacesRegistersFromZeroPageStart()
    {
        var asm = Compile(new MOS6502Target());

        await Assert.That(asm).Contains("ADC $04");
        await Assert.That(asm).Contains("ADC $07");
    }

    [Test]
    public async Task ZeroPageBaseShiftsEveryRegisterAddress()
    {
        var asm = Compile(new ZeroPageBase2Target());

        await Assert.That(asm).Contains("ADC $06");
        await Assert.That(asm).Contains("ADC $09");
        await Assert.That(asm).DoesNotContain("ADC $04");
    }
}
