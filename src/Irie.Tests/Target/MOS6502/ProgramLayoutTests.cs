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

    // The layout with absolute homes allowed (the naive placement's), in a window
    // that ends at `windowEnd`.
    private static string RunWithAbsoluteHomes(string mir, int windowEnd)
    {
        var module = MirModule.Parse(new StringReader(mir));
        var passMgr = new PassManager(null, null);
        passMgr.AddPass(new MOS6502ProgramLayoutPass(
            0, WindowStart, windowEnd, promoteFrameSlots: false, library: null, allowAbsoluteHomes: true));
        passMgr.Run(new CompilationContext(module));
        var writer = new StringWriter();
        module.Write(writer, Target.GetRegisterName);
        return writer.ToString();
    }

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
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                $302 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                $0 = mos6502.lda.zp $302
                mos6502.rts
            }
            func @caller : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                mos6502.jsr.abs @leaf
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
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
    public async Task WithAbsoluteHomes_TheLeastUsedHomeMovesToAbsoluteMemory()
    {
        // One byte of window for two homes: the one used three times keeps it, the
        // one used once lives in a global, and each instruction naming it takes its
        // absolute form (a store's value comes first, then the address).
        var text = RunWithAbsoluteHomes("""
            func @f : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                $0 = mos6502.lda.zp $300
                mos6502.rts
            }
            """, windowEnd: WindowStart + 1);

        await Assert.That(text).Contains("$zp20 = mos6502.sta.zp $a");
        await Assert.That(text).Contains("mos6502.sta.abs $a, @f_h1");
        await Assert.That(text).Contains("$a = mos6502.lda.abs @f_h1");
        await Assert.That(text).Contains("global @f_h1");
        await Assert.That(text).DoesNotContain("$h1");
    }

    [Test]
    public async Task WithAbsoluteHomes_EveryHomeFitsWhenThereIsRoom()
    {
        var text = RunWithAbsoluteHomes("""
            func @f : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                mos6502.rts
            }
            """, windowEnd: WindowEnd);

        await Assert.That(text).DoesNotContain("global");
        await Assert.That(text).DoesNotContain(".abs");
    }

    [Test]
    public async Task WithAbsoluteHomes_ACalleesHomesAreDemotedBeforeTheCallersWhenTheyDoNotFit()
    {
        // @leaf needs 3 bytes and @caller 2, and the window holds 4: the caller's
        // block sits above the leaf's, so something has to give. Both functions
        // still end up with every home somewhere.
        var text = RunWithAbsoluteHomes("""
            func @leaf : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                $302 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                $0 = mos6502.lda.zp $302
                mos6502.rts
            }
            func @caller : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                mos6502.jsr.abs @leaf
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                mos6502.rts
            }
            """, windowEnd: WindowStart + 4);

        await Assert.That(text).DoesNotContain("$h");
        await Assert.That(text).Contains("global");
    }

    // A function with more homes live at the same time than the window has bytes.
    private static string TooManyLiveHomes()
    {
        const int homes = 130;
        var stores = string.Join("\n", Enumerable.Range(0, homes).Select(n => $"    ${300 + n} = mos6502.sta.zp $0"));
        var loads = string.Join("\n", Enumerable.Range(0, homes).Select(n => $"    $0 = mos6502.lda.zp ${300 + n}"));
        return $"func @big : () -> void {{\nbb0():\n{stores}\n{loads}\n    mos6502.rts\n}}\n";
    }

    [Test]
    public async Task WithoutAbsoluteHomes_ExhaustingZeroPageStillFails()
    {
        await Assert.That(() => Run(TooManyLiveHomes())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task ExhaustingZeroPageIsAnExpectedFailure()
    {
        await Assert.That(() => Run(TooManyLiveHomes())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task HomesThatAreNeverLiveTogetherShareAByte()
    {
        // $300 is dead before $301 is written, so both are the first byte of the block.
        var text = Run("""
            func @f : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $301 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $301
                mos6502.rts
            }
            """);

        await Assert.That(text).DoesNotContain("$zp21");
        await Assert.That(text.Split("$zp20 = mos6502.sta.zp $a").Length - 1).IsEqualTo(2);
    }

    [Test]
    public async Task HomesLiveTogetherKeepSeparateBytes()
    {
        var text = Run("""
            func @f : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                $301 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $300
                $0 = mos6502.lda.zp $301
                mos6502.rts
            }
            """);

        await Assert.That(text).Contains("$zp20 = mos6502.sta.zp $a");
        await Assert.That(text).Contains("$zp21 = mos6502.sta.zp $a");
    }

    [Test]
    public async Task AHomeLiveAroundALoopKeepsItsByteFromHomesInTheLoop()
    {
        // $300 is written before the loop and read after it, so it is live through
        // the body, where $301 is written and read each time round.
        var text = Run("""
            func @f : () -> void {
            bb0():
                $300 = mos6502.sta.zp $0
                mos6502.jmp.abs bb1
            bb1():
                [liveins: $300]
                $301 = mos6502.sta.zp $0
                $0 = mos6502.lda.zp $301
                mos6502.bne bb1
                mos6502.jmp.abs bb2
            bb2():
                [liveins: $300]
                $0 = mos6502.lda.zp $300
                mos6502.rts
            }
            """);

        await Assert.That(text).Contains("$zp21 = mos6502.sta.zp $a");
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
                $0 = mos6502.lda.zp $300
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
