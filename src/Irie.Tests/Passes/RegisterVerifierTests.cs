using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// The post-RA verifiers must reject allocator output that would miscompile,
// whatever allocator produced it, and accept well-formed output.
public sealed class RegisterVerifierTests
{
    private static readonly MOS6502Target Target;
    private static readonly PostRegisterAllocationVerifierPass PostRa;

    // Explicit static constructor: constructing the target registers the dialects
    // the text parser needs, and must happen before the first Parse.
    static RegisterVerifierTests()
    {
        Target = new MOS6502Target();
        PostRa = new PostRegisterAllocationVerifierPass(Target.RegisterInfo);
    }

    private static MirFunction Parse(string text) =>
        MirModule.Parse(new StringReader(text)).Functions[0];

    [Test]
    public async Task WellFormedPhysregMir_Passes()
    {
        var fn = Parse("""
            func @f : (i8) -> i8 {
            bb0():
                [liveins: $0]
                mos6502.cmp.imm $0, 0, implicit-def $6, implicit-def $8, implicit-def $5
                mos6502.rts
            }
            """);

        await Assert.That(() => PostRa.Run(fn)).ThrowsNothing();
    }

    [Test]
    public async Task ReadOfUndeclaredLiveIn_Throws()
    {
        var fn = Parse("""
            func @f : () -> void {
            bb0():
                mos6502.cmp.imm $0, 0, implicit-def $6, implicit-def $8, implicit-def $5
                mos6502.rts
            }
            """);

        await Assert.That(() => PostRa.Run(fn)).Throws<MirVerificationException>();
    }

    [Test]
    public async Task ReadOfRegisterWrittenOnOnlyOnePath_Throws()
    {
        var fn = Parse("""
            func @f : (i8) -> void {
            bb0():
                [liveins: $0]
                mos6502.cmp.imm $0, 0, implicit-def $6, implicit-def $8, implicit-def $5
                mos6502.beq bb2, implicit $8
                mos6502.jmp.abs bb1
            bb1():
                $1 = pseudo.copy $0
                mos6502.jmp.abs bb3
            bb2():
                mos6502.jmp.abs bb3
            bb3():
                $0 = pseudo.copy $1
                mos6502.rts
            }
            """);

        await Assert.That(() => PostRa.Run(fn)).Throws<MirVerificationException>();
    }

    [Test]
    public async Task LeftoverVirtualRegister_Throws()
    {
        var fn = new MirFunction("f", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        bb0.LiveIns.Add(MOS6502Registers.A);
        var v = fn.CreateVirtualRegister(IRType.I8);
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.A, IsDefinition: false));

        await Assert.That(() => PostRa.Run(fn)).Throws<MirVerificationException>();
    }

    [Test]
    public async Task TiedOperandsInDifferentRegisters_Throws()
    {
        // eor.imm's result and first use are tied ($a ↔ $a); $x on the use side
        // breaks the tie (and the `ac` class of that slot).
        var fn = Parse("""
            func @f : (i8) -> i8 {
            bb0():
                [liveins: $0, $1]
                $0 = mos6502.eor.imm $1, 5
                mos6502.rts
            }
            """);

        await Assert.That(() => PostRa.Run(fn)).Throws<MirVerificationException>();
    }

    // ---- RegisterAssignmentVerifier ---------------------------------------

    // %v0 = copy $a ; %v1 = copy $x ; $zp2 = copy %v0 ; $zp3 = copy %v1 — both
    // vregs are live across the second copy, so they must not share a register.
    private static (MirFunction fn, LiveIntervals intervals, int v0, int v1) TwoOverlappingVregs()
    {
        var fn = new MirFunction("f", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        bb0.LiveIns.Add(MOS6502Registers.A);
        bb0.LiveIns.Add(MOS6502Registers.X);
        var v0 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");
        var v1 = fn.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

        void Copy(MirOperand dst, MirOperand src) =>
            bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), dst, src);

        Copy(new VirtualReg(v0, true), new PhysicalReg(MOS6502Registers.A, false));
        Copy(new VirtualReg(v1, true), new PhysicalReg(MOS6502Registers.X, false));
        Copy(new PhysicalReg(MOS6502Registers.RC(2), true), new VirtualReg(v0, false));
        Copy(new PhysicalReg(MOS6502Registers.RC(3), true), new VirtualReg(v1, false));

        return (fn, new LiveIntervalsAnalysis().Compute(fn), v0, v1);
    }

    [Test]
    public async Task SimultaneouslyLiveVregsInDistinctRegisters_Passes()
    {
        var (fn, intervals, v0, v1) = TwoOverlappingVregs();
        var assignment = new Dictionary<int, int>
        {
            [v0] = MOS6502Registers.RC(4),
            [v1] = MOS6502Registers.RC(5),
        };

        await Assert.That(() =>
            RegisterAssignmentVerifier.Verify(fn, Target.RegisterInfo, intervals, assignment)).ThrowsNothing();
    }

    [Test]
    public async Task SimultaneouslyLiveVregsSharingARegister_Throws()
    {
        var (fn, intervals, v0, v1) = TwoOverlappingVregs();
        var assignment = new Dictionary<int, int>
        {
            [v0] = MOS6502Registers.RC(4),
            [v1] = MOS6502Registers.RC(4),
        };

        await Assert.That(() =>
            RegisterAssignmentVerifier.Verify(fn, Target.RegisterInfo, intervals, assignment))
            .Throws<MirVerificationException>();
    }

    [Test]
    public async Task VregAssignedARegisterThatIsClobberedWhileItIsLive_Throws()
    {
        var (fn, intervals, v0, v1) = TwoOverlappingVregs();
        // $zp2 is written by the third copy while %v1 is still live.
        var assignment = new Dictionary<int, int>
        {
            [v0] = MOS6502Registers.RC(4),
            [v1] = MOS6502Registers.RC(2),
        };

        await Assert.That(() =>
            RegisterAssignmentVerifier.Verify(fn, Target.RegisterInfo, intervals, assignment))
            .Throws<MirVerificationException>();
    }
}
