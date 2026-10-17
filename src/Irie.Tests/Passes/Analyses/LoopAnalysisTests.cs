using Irie.Mir;
using Irie.Passes.Analyses;

namespace Irie.Tests.Passes.Analyses;

public sealed class LoopAnalysisTests
{
    private static (MirFunction Function, LoopInfo Info) Analyse(string mir)
    {
        var function = MirModule.Parse(new StringReader(mir)).Functions[0];
        return (function, new LoopAnalysis().Compute(function));
    }

    [Test]
    public async Task Diamond_HasNoLoops()
    {
        var (function, info) = Analyse("""
            func @f : (i1) -> void {
            bb0(%0 : i1):
                cf.cond_br %0, bb1(), bb2()
            bb1():
                cf.br bb3()
            bb2():
                cf.br bb3()
            bb3():
                core.return
            }
            """);

        await Assert.That(info.Loops.Count).IsEqualTo(0);
        await Assert.That(function.Blocks.All(b => info.Frequency(b) == 1)).IsTrue();
    }

    [Test]
    public async Task NestedLoops_HaveIncreasingDepthAndFrequency()
    {
        // bb1 is the outer header, bb2 the inner header with a self-loop body bb3.
        var (function, info) = Analyse("""
            func @f : (i1) -> void {
            bb0(%0 : i1):
                cf.br bb1()
            bb1():
                cf.br bb2()
            bb2():
                cf.cond_br %0, bb3(), bb4()
            bb3():
                cf.br bb2()
            bb4():
                cf.cond_br %0, bb1(), bb5()
            bb5():
                core.return
            }
            """);
        var b = function.Blocks;

        await Assert.That(info.Loops.Count).IsEqualTo(2);
        await Assert.That(new[] { 0, 1, 2, 3, 4, 5 }.Select(i => info.Depth(b[i])).ToArray())
            .IsEquivalentTo(new[] { 0, 1, 2, 2, 1, 0 });
        await Assert.That(info.Frequency(b[0])).IsEqualTo(1L);
        await Assert.That(info.Frequency(b[1])).IsEqualTo(8L);
        await Assert.That(info.Frequency(b[3])).IsEqualTo(64L);
        await Assert.That(info.InnermostLoop(b[3])!.Header).IsEqualTo(b[2]);
        await Assert.That(info.InnermostLoop(b[3])!.Parent!.Header).IsEqualTo(b[1]);
    }

    [Test]
    public async Task SelfLoop_IsALoopOfOneBlock()
    {
        var (function, info) = Analyse("""
            func @f : (i1) -> void {
            bb0(%0 : i1):
                cf.br bb1()
            bb1():
                cf.cond_br %0, bb1(), bb2()
            bb2():
                core.return
            }
            """);

        await Assert.That(info.Loops.Count).IsEqualTo(1);
        await Assert.That(info.Loops[0].Blocks.Count).IsEqualTo(1);
        await Assert.That(info.Depth(function.Blocks[1])).IsEqualTo(1);
    }

    [Test]
    public async Task TwoLatches_FormOneLoop()
    {
        var (function, info) = Analyse("""
            func @f : (i1) -> void {
            bb0(%0 : i1):
                cf.br bb1()
            bb1():
                cf.cond_br %0, bb2(), bb3()
            bb2():
                cf.cond_br %0, bb1(), bb3()
            bb3():
                cf.cond_br %0, bb1(), bb4()
            bb4():
                core.return
            }
            """);

        await Assert.That(info.Loops.Count).IsEqualTo(1);
        await Assert.That(info.Loops[0].Latches.Count).IsEqualTo(2);
        await Assert.That(info.Loops[0].Blocks.Count).IsEqualTo(3);
        await Assert.That(info.Depth(function.Blocks[4])).IsEqualTo(0);
    }

    [Test]
    public async Task IrreducibleCycle_IsNotALoop()
    {
        // bb1 and bb2 can each be entered from bb0, so neither dominates the other.
        var (_, info) = Analyse("""
            func @f : (i1) -> void {
            bb0(%0 : i1):
                cf.cond_br %0, bb1(), bb2()
            bb1():
                cf.cond_br %0, bb2(), bb3()
            bb2():
                cf.cond_br %0, bb1(), bb3()
            bb3():
                core.return
            }
            """);

        await Assert.That(info.Loops.Count).IsEqualTo(0);
    }
}
