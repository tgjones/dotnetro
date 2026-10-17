using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

// Whole-program zero-page layout of function-local homes ($300, $301, …): blocks
// stacked above the callees', shared inside call cycles, saved around calls that
// re-enter the caller. The tests feed post-RA MIR (homes already assigned) through
// the recursion-save and layout passes and read the addresses back from the code.
public sealed class ProgramLayoutTests
{
    private const int WindowStart = 0x14;
    private const int WindowEnd = 0x90;

    private static readonly MOS6502Target Target;

    static ProgramLayoutTests() => Target = new MOS6502Target();

    private static string Run(string mir, params Pass[] extra) => Run(mir, null, extra);

    private static string Run(string mir, Irie.MachineCode.MachineCodeModule? library, params Pass[] extra)
    {
        var module = MirModule.Parse(new StringReader(mir));
        var passMgr = new PassManager(null, null);
        foreach (var pass in extra)
            passMgr.AddPass(pass);
        passMgr.AddPass(new MOS6502ProgramLayoutPass(0, WindowStart, WindowEnd, promoteFrameSlots: false, library));
        passMgr.Run(new CompilationContext(module));
        var writer = new StringWriter();
        module.Write(writer, Target.GetRegisterName);
        return writer.ToString();
    }

    [Test]
    public async Task CallerBlockSitsAboveCalleeBlock()
    {
        var text = Run("""
            func @leaf : () -> void {
            bb0():
                $302 = mos6502.sta.zp $0
                mos6502.rts
            }
            func @caller : () -> void {
            bb0():
                $301 = mos6502.sta.zp $0
                mos6502.jsr.abs @leaf
                mos6502.rts
            }
            """);

        // @leaf has 3 homes ($300..$302) at $14, so its $302 is $16; @caller's block
        // starts at $17 and its $301 is $18.
        await Assert.That(text).Contains("$zp22 = mos6502.sta.zp $a");
        await Assert.That(text).Contains("$zp24 = mos6502.sta.zp $a");
    }

    [Test]
    public async Task CallerOfALibraryRoutineSitsAboveItsDeclaredFootprint()
    {
        var library = MOS6502AssemblyParser.Parse(new StringReader("""
            __helper:
            .zeropage 4
                RTS
            __entry:
                JMP __helper
            """));
        var text = Run("""
            func @caller : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.jsr.abs @__entry
                mos6502.rts
            }
            func @other : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.rts
            }
            """, library);

        // The routine scratches the four bytes at the window start ($14..$17),
        // reached through __entry's jump; @caller is placed above them, while
        // @other, which does not call it, still starts at the window start.
        await Assert.That(text).Contains("$zp24 = mos6502.sta.zp $a");
        await Assert.That(text).Contains("$zp20 = mos6502.sta.zp $a");
    }

    [Test]
    public async Task IndependentFunctionsShareTheSameBytes()
    {
        var text = Run("""
            func @a : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.rts
            }
            func @b : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.rts
            }
            """);

        var first = text.IndexOf("$zp20 = mos6502.sta.zp $a", StringComparison.Ordinal);
        await Assert.That(first).IsGreaterThanOrEqualTo(0);
        await Assert.That(text.IndexOf("$zp20 = mos6502.sta.zp $a", first + 1, StringComparison.Ordinal)).IsGreaterThan(first);
    }

    [Test]
    public async Task ExhaustingZeroPageIsAnExpectedFailure()
    {
        await Assert.That(() => Run("""
            func @big : () -> void {
            bb0():
                $430 = mos6502.sta.zp $0
                mos6502.rts
            }
            """)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task HomeLiveAcrossSelfRecursiveCallIsSavedAndRestored()
    {
        var text = Run("""
            func @recur : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.jsr.abs @recur
                $0 = mos6502.lda.zp $300
                mos6502.rts
            }
            """, new MOS6502RecursionSavePass());

        // lda $14 ; pha ; jsr ; pla ; sta $14
        await Assert.That(text).Contains("mos6502.pha");
        await Assert.That(text).Contains("mos6502.pla");
        await Assert.That(text).Contains("$zp20 = mos6502.sta.zp $a");
    }

    [Test]
    public async Task NonRecursiveCallSavesNothing()
    {
        var text = Run("""
            func @leaf : () -> void {
            bb0():
                mos6502.rts
            }
            func @caller : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.jsr.abs @leaf
                $0 = mos6502.lda.zp $300
                mos6502.rts
            }
            """, new MOS6502RecursionSavePass());

        await Assert.That(text).DoesNotContain("mos6502.pha");
    }

    [Test]
    public async Task MutuallyRecursiveFunctionsShareOneBlock()
    {
        var text = Run("""
            func @ping : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.jsr.abs @pong
                $0 = mos6502.lda.zp $300
                mos6502.rts
            }
            func @pong : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                mos6502.jsr.abs @ping
                $0 = mos6502.lda.zp $301
                mos6502.rts
            }
            """, new MOS6502RecursionSavePass());

        // Both blocks start at $14, overlapping, because every call inside the
        // cycle saves the live homes.
        await Assert.That(text.Split("$zp20 = mos6502.sta.zp $a").Length - 1).IsGreaterThanOrEqualTo(2);
        await Assert.That(text).Contains("$zp21 = mos6502.sta.zp $a");
        await Assert.That(text).Contains("mos6502.pha");
    }
}
