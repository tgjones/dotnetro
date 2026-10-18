using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes.Analyses;

public sealed class CallGraphTests
{
    private static readonly MOS6502Target Target;

    static CallGraphTests() => Target = new MOS6502Target();

    // A function whose address bytes are loaded (a delegate, a function pointer) is
    // address-taken, not called: an indirect call can then reach it.
    [Test]
    public async Task LoadingTheAddressBytesOfAFunctionTakesItsAddress()
    {
        var module = MirModule.Parse(new StringReader("""
            func @target : () -> void {
            bb0():
                mos6502.rts
            }
            func @taker : () -> void {
            bb0():
                $0 = mos6502.lda.imm.symlo @target
                $1 = mos6502.ldx.imm.symhi @target
                mos6502.rts
            }
            func @dispatcher : () -> void {
            bb0():
                mos6502.jsr.abs @__call_indirect_trampoline
                mos6502.rts
            }
            """));

        var graph = CallGraph.Build(module);
        var target = graph.IndexByName["target"];

        await Assert.That(graph.AddressTaken[target]).IsTrue();
        await Assert.That(graph.DirectCallees[graph.IndexByName["taker"]]).DoesNotContain(target);
        await Assert.That(graph.Callees[graph.IndexByName["dispatcher"]]).Contains(target);
    }

    [Test]
    public async Task AJsrToAFunctionIsACall()
    {
        var module = MirModule.Parse(new StringReader("""
            func @callee : () -> void {
            bb0():
                mos6502.rts
            }
            func @caller : () -> void {
            bb0():
                mos6502.jsr.abs @callee
                mos6502.rts
            }
            """));

        var graph = CallGraph.Build(module);

        await Assert.That(graph.DirectCallees[graph.IndexByName["caller"]]).Contains(graph.IndexByName["callee"]);
        await Assert.That(graph.AddressTaken[graph.IndexByName["callee"]]).IsFalse();
    }
}
