using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

// The placement verifier must reject any assignment that makes an instruction
// read the wrong value, and accept the correct ones, whatever the shape of the
// control flow. Each test builds vreg-level code, snapshots it the way the
// allocator does, applies a hand-written assignment and verifies.
public sealed class PlacementVerifierTests
{
    private static readonly MOS6502Target Target;

    // Explicit static constructor: constructing the target registers the dialects
    // whose ids the builders below use, and must happen before the first of them.
    static PlacementVerifierTests() => Target = new MOS6502Target();

    private const int A = MOS6502Registers.A;
    private const int X = MOS6502Registers.X;

    private static int Zp(int n) => MOS6502Registers.RC(n);

    private static void Copy(MirBlock block, int dst, int src) =>
        block.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), new VirtualReg(dst, true), new VirtualReg(src, false));

    private static void Jump(MirBlock block, MirBlock target) =>
        block.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.JmpAbs), new BlockTarget(target, []));

    private static int NewValue(MirFunction function) =>
        function.CreateVirtualRegisterInClass(MOS6502RegisterClass.Anyi8, "any8");

    // `v = copy $reg` for an incoming register.
    private static int Incoming(MirFunction function, MirBlock block, int register)
    {
        var v = NewValue(function);
        block.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy),
            new VirtualReg(v, true), new PhysicalReg(register, false));
        return v;
    }

    private static void Verify(MirFunction function, Dictionary<int, int> assignment, Dictionary<int, int>? reloads = null)
    {
        var annotation = PlacementAnnotation.FromVirtualRegisters(function);
        foreach (var (reload, spilled) in reloads ?? [])
            annotation.ReloadSources[reload] = spilled;

        foreach (var block in function.Blocks)
            foreach (var instruction in block.Instructions)
                for (var i = 0; i < instruction.Operands.Length; i++)
                    if (instruction.Operands[i] is VirtualReg v)
                        instruction.Operands[i] = new PhysicalReg(assignment[v.Id], v.IsDefinition);

        PlacementVerifier.Verify(function, Target.RegisterInfo, annotation);
    }

    [Test]
    public async Task ValuesInDistinctRegisters_Pass()
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        var a = Incoming(f, bb0, A);
        var x = Incoming(f, bb0, X);
        var c = NewValue(f);
        var d = NewValue(f);
        Copy(bb0, c, a);
        Copy(bb0, d, x);

        await Assert.That(() => Verify(f, new() { [a] = Zp(2), [x] = Zp(3), [c] = Zp(4), [d] = Zp(5) })).ThrowsNothing();
    }

    [Test]
    public async Task TwoLiveValuesInOneRegister_Throws()
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        var a = Incoming(f, bb0, A);
        var x = Incoming(f, bb0, X);
        var c = NewValue(f);
        var d = NewValue(f);
        Copy(bb0, c, a); // a is read after x has overwritten its register
        Copy(bb0, d, x);
        Copy(bb0, NewValue(f), a);

        var extra = f.VirtualRegisterIds.Max();
        await Assert.That(() => Verify(f, new() { [a] = Zp(2), [x] = Zp(2), [c] = Zp(4), [d] = Zp(5), [extra] = Zp(6) }))
            .Throws<MirVerificationException>();
    }

    [Test]
    public async Task ClobberBetweenDefinitionAndUse_Throws()
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        var a = Incoming(f, bb0, A);
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Rts)); // placeholder for an unrelated instruction
        // The clobber: something defines $rc2 without naming a value.
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), new PhysicalReg(Zp(2), true), new PhysicalReg(X, false));
        var c = NewValue(f);
        Copy(bb0, c, a);

        await Assert.That(() => Verify(f, new() { [a] = Zp(2), [c] = Zp(3) })).Throws<MirVerificationException>();
    }

    [Test]
    public async Task JoinWhereOnePathOverwritesTheRegister_Throws()
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        var left = f.CreateBlock();
        var right = f.CreateBlock();
        var join = f.CreateBlock();
        var a = Incoming(f, bb0, A);
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Beq), new BlockTarget(left, []));
        Jump(bb0, right);
        left.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), new PhysicalReg(Zp(2), true), new PhysicalReg(X, false));
        Jump(left, join);
        Jump(right, join);
        Copy(join, NewValue(f), a);

        await Assert.That(() => Verify(f, new() { [a] = Zp(2), [f.VirtualRegisterIds.Max()] = Zp(3) }))
            .Throws<MirVerificationException>();
    }

    [Test]
    public async Task JoinWhereBothPathsKeepTheValue_Passes()
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        var left = f.CreateBlock();
        var right = f.CreateBlock();
        var join = f.CreateBlock();
        var a = Incoming(f, bb0, A);
        bb0.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Beq), new BlockTarget(left, []));
        Jump(bb0, right);
        Jump(left, join);
        Jump(right, join);
        var c = NewValue(f);
        Copy(join, c, a);

        await Assert.That(() => Verify(f, new() { [a] = Zp(2), [c] = Zp(3) })).ThrowsNothing();
    }

    [Test]
    public async Task ValueKeptAroundALoop_Passes_AndClobberInTheLoopBody_Throws()
    {
        MirFunction Build(bool clobber, out int a, out int c)
        {
            var f = new MirFunction("f", [], IRType.Void);
            var bb0 = f.CreateBlock();
            var loop = f.CreateBlock();
            var exit = f.CreateBlock();
            a = Incoming(f, bb0, A);
            Jump(bb0, loop);
            c = NewValue(f);
            Copy(loop, c, a); // reads a on every iteration
            if (clobber)
                loop.AddInstruction(PseudoDialect.OpRef(PseudoOp.Copy), new PhysicalReg(Zp(2), true), new PhysicalReg(X, false));
            loop.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Beq), new BlockTarget(loop, []));
            Jump(loop, exit);
            exit.AddInstruction(MOS6502Dialect.OpRef(MOS6502Op.Rts));
            return f;
        }

        var good = Build(clobber: false, out var a1, out var c1);
        await Assert.That(() => Verify(good, new() { [a1] = Zp(2), [c1] = Zp(3) })).ThrowsNothing();

        var bad = Build(clobber: true, out var a2, out var c2);
        await Assert.That(() => Verify(bad, new() { [a2] = Zp(2), [c2] = Zp(3) })).Throws<MirVerificationException>();
    }

    private static MirFunction SpillAndReload(out int a, out int reloaded, bool clobberSlot)
    {
        var f = new MirFunction("f", [], IRType.Void);
        var bb0 = f.CreateBlock();
        a = Incoming(f, bb0, A);
        var other = Incoming(f, bb0, X);
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Spill), new Immediate(0), new VirtualReg(a, false));
        if (clobberSlot)
            bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Spill), new Immediate(0), new VirtualReg(other, false));
        reloaded = NewValue(f);
        bb0.AddInstruction(PseudoDialect.OpRef(PseudoOp.Reload), new VirtualReg(reloaded, true), new Immediate(0));
        Copy(bb0, NewValue(f), reloaded);
        return f;
    }

    [Test]
    public async Task ReloadOfTheSpilledValue_Passes()
    {
        var f = SpillAndReload(out var a, out var reloaded, clobberSlot: false);
        var all = f.VirtualRegisterIds.ToList();
        var assignment = all.ToDictionary(v => v, v => Zp(2 + v));

        await Assert.That(() => Verify(f, assignment, new() { [reloaded] = a })).ThrowsNothing();
    }

    [Test]
    public async Task ReloadAfterTheSlotWasOverwritten_Throws()
    {
        var f = SpillAndReload(out var a, out var reloaded, clobberSlot: true);
        var assignment = f.VirtualRegisterIds.ToDictionary(v => v, v => Zp(2 + v));

        await Assert.That(() => Verify(f, assignment, new() { [reloaded] = a })).Throws<MirVerificationException>();
    }
}
