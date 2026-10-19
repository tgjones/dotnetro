using Irie.Dialects.Arith;
using Irie.Dialects.Call;
using Irie.Dialects.Cast;
using Irie.Dialects.Mem;
using Irie.Mir;
using Irie.Passes;
using Irie.Target.MOS6502;

namespace Irie.Tests.Passes;

/// <summary>
/// The pointer typing rules of GenericMirVerifierPass: a `ptr` is not an integer,
/// and every op that takes or produces an address says so. One rejection test per
/// rule, plus a well-typed function that is accepted.
/// </summary>
public sealed class GenericMirVerifierPointerTests
{
    private static readonly GenericMirVerifierPass Pass = new();

    static GenericMirVerifierPointerTests()
    {
        _ = new MOS6502Target();
    }

    private static VirtualReg Def(int vreg) => new(vreg, IsDefinition: true);
    private static VirtualReg Use(int vreg) => new(vreg, IsDefinition: false);

    /// <summary>
    /// Builds `func @f : () -&gt; void` with one block and lets `body` fill it; the
    /// rule under test sees only what `body` emits.
    /// </summary>
    private static MirModule Module(Action<MirFunction, MirBlock> body)
    {
        var module = new MirModule();
        var fn = new MirFunction("f", [], IRType.Void);
        var bb0 = fn.CreateBlock();
        body(fn, bb0);
        module.Functions.Add(fn);
        return module;
    }

    private static async Task AssertRejected(MirModule module, string messageFragment)
    {
        var exception = await Assert.That(() => Pass.Run(new CompilationContext(module)))
            .Throws<MirVerificationException>();
        await Assert.That(exception!.Message).Contains(messageFragment);
    }

    [Test]
    public async Task WellTypedPointerCode_DoesNotThrow()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            var off = fn.CreateVirtualRegister(IRType.I16);
            var q = fn.CreateVirtualRegister(IRType.Pointer);
            var v = fn.CreateVirtualRegister(IRType.I8);
            var raw = fn.CreateVirtualRegister(IRType.I16);
            var back = fn.CreateVirtualRegister(IRType.Pointer);
            var loaded = fn.CreateVirtualRegister(IRType.Pointer);

            bb0.AddInstruction(MemDialect.OpRef(MemOp.Symbol), Def(p), new Symbol("g"));
            bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant), Def(off), new Immediate(2));
            bb0.AddInstruction(MemDialect.OpRef(MemOp.PtrAdd), Def(q), Use(p), Use(off));
            bb0.AddInstruction(MemDialect.OpRef(MemOp.LoadI8), Def(v), Use(q));
            bb0.AddInstruction(MemDialect.OpRef(MemOp.StoreI8), Use(q), Use(v));
            bb0.AddInstruction(CastDialect.OpRef(CastOp.PtrToInt), Def(raw), Use(q));
            bb0.AddInstruction(CastDialect.OpRef(CastOp.IntToPtr), Def(back), Use(raw));
            bb0.AddInstruction(MemDialect.OpRef(MemOp.LoadPtr), Def(loaded), Use(back));
            bb0.AddInstruction(MemDialect.OpRef(MemOp.StorePtr), Use(back), Use(loaded));
        });

        await Assert.That(() => Pass.Run(new CompilationContext(module))).ThrowsNothing();
    }

    [Test]
    public async Task LoadWithIntegerAddress_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var addr = fn.CreateVirtualRegister(IRType.I16);
            var v = fn.CreateVirtualRegister(IRType.I8);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.LoadI8), Def(v), Use(addr));
        });
        await AssertRejected(module, "mem.load.i8 address %0 has type i16, expected ptr");
    }

    [Test]
    public async Task StoreWithIntegerAddress_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var addr = fn.CreateVirtualRegister(IRType.I16);
            var v = fn.CreateVirtualRegister(IRType.I8);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.StoreI8), Use(addr), Use(v));
        });
        await AssertRejected(module, "mem.store.i8 address %0 has type i16, expected ptr");
    }

    [Test]
    public async Task FillWithIntegerAddress_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var addr = fn.CreateVirtualRegister(IRType.I16);
            var b = fn.CreateVirtualRegister(IRType.I8);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.Fill), Use(addr), Use(b), new Immediate(4));
        });
        await AssertRejected(module, "mem.fill address %0 has type i16, expected ptr");
    }

    [Test]
    public async Task CallIndirectWithIntegerTarget_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var target = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(CallDialect.OpRef(CallOp.Indirect), Use(target));
        });
        await AssertRejected(module, "call.indirect target %0 has type i16, expected ptr");
    }

    [Test]
    public async Task SymbolDefiningInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.Symbol), Def(p), new Symbol("g"));
        });
        await AssertRejected(module, "mem.symbol result %0 has type i16, expected ptr");
    }

    [Test]
    public async Task FrameAddrDefiningInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.FrameAddr), Def(p), new Immediate(0));
        });
        await AssertRejected(module, "mem.frame_addr result %0 has type i16, expected ptr");
    }

    [Test]
    public async Task PtrAddWithIntegerBase_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.I16);
            var off = fn.CreateVirtualRegister(IRType.I16);
            var q = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.PtrAdd), Def(q), Use(p), Use(off));
        });
        await AssertRejected(module, "mem.ptradd base %0 has type i16, expected ptr");
    }

    [Test]
    public async Task PtrAddWithWrongOffsetWidth_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            var off = fn.CreateVirtualRegister(IRType.I32);
            var q = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.PtrAdd), Def(q), Use(p), Use(off));
        });
        await AssertRejected(module, "mem.ptradd offset %1 has type i32, expected i16");
    }

    [Test]
    public async Task PtrAddDefiningInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            var off = fn.CreateVirtualRegister(IRType.I16);
            var q = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.PtrAdd), Def(q), Use(p), Use(off));
        });
        await AssertRejected(module, "mem.ptradd result %2 has type i16, expected ptr");
    }

    [Test]
    public async Task PtrToIntOfInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var x = fn.CreateVirtualRegister(IRType.I16);
            var y = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(CastDialect.OpRef(CastOp.PtrToInt), Def(y), Use(x));
        });
        await AssertRejected(module, "cast.ptrtoint operand %0 has type i16, expected ptr");
    }

    [Test]
    public async Task PtrToIntDefiningWrongWidth_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            var y = fn.CreateVirtualRegister(IRType.I8);
            bb0.AddInstruction(CastDialect.OpRef(CastOp.PtrToInt), Def(y), Use(p));
        });
        await AssertRejected(module, "cast.ptrtoint result %1 has type i8, expected i16");
    }

    [Test]
    public async Task IntToPtrOfWrongWidth_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var x = fn.CreateVirtualRegister(IRType.I8);
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(CastDialect.OpRef(CastOp.IntToPtr), Def(p), Use(x));
        });
        await AssertRejected(module, "cast.inttoptr operand %0 has type i8, expected i16");
    }

    [Test]
    public async Task LoadPtrDefiningInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var addr = fn.CreateVirtualRegister(IRType.Pointer);
            var v = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.LoadPtr), Def(v), Use(addr));
        });
        await AssertRejected(module, "mem.load.ptr result %1 has type i16, expected ptr");
    }

    [Test]
    public async Task StorePtrOfInteger_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var addr = fn.CreateVirtualRegister(IRType.Pointer);
            var v = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(MemDialect.OpRef(MemOp.StorePtr), Use(addr), Use(v));
        });
        await AssertRejected(module, "mem.store.ptr stored value %1 has type i16, expected ptr");
    }

    [Test]
    public async Task ArithOnPointerOperand_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            var n = fn.CreateVirtualRegister(IRType.I16);
            var sum = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(ArithDialect.OpRef(ArithOp.AddI), Def(sum), Use(p), Use(n));
        });
        await AssertRejected(module, "arith.addi operand %0 has type ptr");
    }

    [Test]
    public async Task ArithProducingPointer_Throws()
    {
        var module = Module((fn, bb0) =>
        {
            var p = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(ArithDialect.OpRef(ArithOp.Constant), Def(p), new Immediate(0));
        });
        await AssertRejected(module, "arith.constant result %0 has type ptr");
    }

    /// <summary>
    /// A module with @callee : (ptr, i8) -&gt; ptr and a caller whose call.func
    /// arguments / results `configure` shapes.
    /// </summary>
    private static MirModule CallModule(Action<MirFunction, MirBlock> configureCaller)
    {
        var module = new MirModule();
        module.Functions.Add(new MirFunction("callee", [IRType.Pointer, IRType.I8], IRType.Pointer));
        var caller = new MirFunction("caller", [], IRType.Void);
        configureCaller(caller, caller.CreateBlock());
        module.Functions.Add(caller);
        return module;
    }

    [Test]
    public async Task CallArgumentTypeMismatch_Throws()
    {
        var module = CallModule((fn, bb0) =>
        {
            var a = fn.CreateVirtualRegister(IRType.I16);
            var b = fn.CreateVirtualRegister(IRType.I8);
            var r = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(CallDialect.OpRef(CallOp.Func), Def(r), new Symbol("callee"), Use(a), Use(b));
        });
        await AssertRejected(module,
            "call.func @callee argument 0 %0 has type i16, but the callee's signature declares ptr");
    }

    [Test]
    public async Task CallArgumentCountMismatch_Throws()
    {
        var module = CallModule((fn, bb0) =>
        {
            var a = fn.CreateVirtualRegister(IRType.Pointer);
            var r = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(CallDialect.OpRef(CallOp.Func), Def(r), new Symbol("callee"), Use(a));
        });
        await AssertRejected(module, "call.func @callee passes 1 argument(s) but the callee takes 2");
    }

    [Test]
    public async Task CallResultTypeMismatch_Throws()
    {
        var module = CallModule((fn, bb0) =>
        {
            var a = fn.CreateVirtualRegister(IRType.Pointer);
            var b = fn.CreateVirtualRegister(IRType.I8);
            var r = fn.CreateVirtualRegister(IRType.I16);
            bb0.AddInstruction(CallDialect.OpRef(CallOp.Func), Def(r), new Symbol("callee"), Use(a), Use(b));
        });
        await AssertRejected(module,
            "call.func @callee result %2 has type i16, but the callee's signature declares ptr");
    }

    [Test]
    public async Task CallMatchingSignature_DoesNotThrow()
    {
        var module = CallModule((fn, bb0) =>
        {
            var a = fn.CreateVirtualRegister(IRType.Pointer);
            var b = fn.CreateVirtualRegister(IRType.I8);
            var r = fn.CreateVirtualRegister(IRType.Pointer);
            bb0.AddInstruction(CallDialect.OpRef(CallOp.Func), Def(r), new Symbol("callee"), Use(a), Use(b));
        });
        await Assert.That(() => Pass.Run(new CompilationContext(module))).ThrowsNothing();
    }
}
