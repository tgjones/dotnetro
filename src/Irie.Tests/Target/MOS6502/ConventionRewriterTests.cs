using Irie.Mir;
using Irie.Passes;
using Irie.Target;
using Irie.Target.MOS6502;

namespace Irie.Tests.Target.MOS6502;

/// <summary>
/// Moving a function's arguments and results to other registers renames the ABI copies
/// of the function and of the calls to it, and nothing else.
/// </summary>
public sealed class ConventionRewriterTests
{
    private static readonly MOS6502Target Target = new();

    private const string Program = """
        func @g : (i16, i8) -> i16 {
        bb0(%0 : i16, %1 : i8):
            core.return %0
        }
        func @f : (i16) -> i16 {
        bb0(%0 : i16):
            %1 : i8 = arith.constant 7
            %2 : i16 = call.func @g, %0, %1
            core.return %2
        }
        """;

    private static (MirModule Module, string Text) Lower(Action<MirModule>? rewrite)
    {
        var module = MirModule.Parse(new StringReader(Program));
        var passMgr = new PassManager(null, null);
        passMgr.AddPass(new AbiLoweringPass(Target));
        passMgr.Run(new CompilationContext(module));
        rewrite?.Invoke(module);
        var writer = new StringWriter();
        module.Write(writer, Target.GetRegisterName);
        return (module, writer.ToString());
    }

    private static FunctionConvention Convention(int[] args, int[] results) => new(args, results);

    [Test]
    public async Task DefaultConventionIsA_X_ThenZeroPage()
    {
        var (module, _) = Lower(null);
        var g = MOS6502ConventionRewriter.DefaultFor(module.Functions[0]);
        await Assert.That(g.ArgBytes).IsEquivalentTo(new[] { MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.RC(2) });
        await Assert.That(g.ReturnBytes).IsEquivalentTo(new[] { MOS6502Registers.A, MOS6502Registers.X });
    }

    [Test]
    public async Task RewritingToTheCurrentConventionChangesNothing()
    {
        var (_, before) = Lower(null);
        var (_, after) = Lower(m =>
            MOS6502ConventionRewriter.Apply(m, m.Functions[0], MOS6502ConventionRewriter.DefaultFor(m.Functions[0])));
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task CalleeAndCallSiteFollowTheNewConvention()
    {
        var chosen = Convention(
            [MOS6502Registers.RC(5), MOS6502Registers.A, MOS6502Registers.X],
            [MOS6502Registers.X, MOS6502Registers.RC(3)]);
        var (module, text) = Lower(m => MOS6502ConventionRewriter.Apply(m, m.Functions[0], chosen));

        await Assert.That(module.Functions[0].Convention).IsSameReferenceAs(chosen);
        var g = text[..text.IndexOf("func @f", StringComparison.Ordinal)];
        var f = text[text.IndexOf("func @f", StringComparison.Ordinal)..];

        // g: the entry copies read the new argument registers, the return copies write the new result registers.
        await Assert.That(g).Contains("[liveins: $zp5, $a, $x]");
        await Assert.That(g).Contains("$x = pseudo.copy");
        await Assert.That(g).Contains("$zp3 = pseudo.copy");
        await Assert.That(g).DoesNotContain("$zp2");

        // f: the argument copies write them, the result copies read them, the jsr's uses follow.
        await Assert.That(f).Contains("$zp5 = pseudo.copy");
        await Assert.That(f).Contains("mos6502.jsr.abs @g, implicit $zp5, implicit $a, implicit $x,");
        await Assert.That(f).Contains("= pseudo.copy $x");
        await Assert.That(f).Contains("= pseudo.copy $zp3");
    }

    [Test]
    public async Task ConventionsThatDoNotFitAreRejected()
    {
        var (module, _) = Lower(null);
        var g = module.Functions[0];
        // Y is not a convention register; a register twice; the wrong number of bytes.
        await Assert.That(MOS6502ConventionRewriter.IsValidFor(
            Convention([MOS6502Registers.Y, MOS6502Registers.X, MOS6502Registers.RC(2)], [MOS6502Registers.A, MOS6502Registers.X]), g)).IsFalse();
        await Assert.That(MOS6502ConventionRewriter.IsValidFor(
            Convention([MOS6502Registers.A, MOS6502Registers.A, MOS6502Registers.RC(2)], [MOS6502Registers.A, MOS6502Registers.X]), g)).IsFalse();
        await Assert.That(MOS6502ConventionRewriter.IsValidFor(
            Convention([MOS6502Registers.A], [MOS6502Registers.A, MOS6502Registers.X]), g)).IsFalse();
    }
}
