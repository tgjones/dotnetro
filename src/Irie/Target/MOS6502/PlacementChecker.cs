using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// Translation validation of one placed block, independent of how it was placed.
//
// Both the block as shape selection left it (unpinned byte operations on typed
// vregs) and the code a placement emitted for it (registers, homes, concrete
// opcodes) are run symbolically. Each value is a term over what the block starts
// with (the inputs: the live-in vregs, read from their homes, and for the entry
// block A, X, Y and the zero-page registers of the calling convention). The two
// runs have to agree on everything the rest of the program can observe:
//
//   - the stores and calls, in order, with the addresses, values and arguments;
//   - the conditional branches, with the flags they test;
//   - on every exit (a jump, a taken branch, through a block of edge moves), the
//     home of every vreg live into the successor, and of every parameter it
//     takes, holds the value it should;
//   - the return registers at an rts.
//
// A wrong register, a clobbered operand, a flag overwritten before its branch or
// an edge move that reads a stale home all show up as terms that differ, whichever
// pass produced the code. Terms are hash-consed, so equality is identity; the
// commutative operations order their operands; `inc`/`dec` are an add / subtract
// of one, and `asl`/`rol` are the same shift with a known or unknown carry in.
internal sealed class PlacementChecker(MOS6502Placer placer)
{
    private const int A = MOS6502Registers.A;
    private const int X = MOS6502Registers.X;
    private const int Y = MOS6502Registers.Y;

    // ------------------------------------------------------------------
    // Terms
    // ------------------------------------------------------------------

    private sealed class Term
    {
        public required int Id;
        public required string Kind;
        public long Num;
        public string? Text;
        public Term[] Args = [];

        public override string ToString() => Kind switch
        {
            "c" => $"#{Num:X2}",
            "in" => $"<{Text}>",
            "sym" => $"{(Num == 0 ? "<" : ">")}{Text}",
            "unk" => $"?{Num}",
            "ret" => $"ret{Num}.{Text}",
            "addr" => $"@{Text}+{Num}",
            "load" => $"load[{Num}]({Args[0]})",
            _ => $"{Kind}({string.Join(", ", Args.Select(a => a.ToString()))})",
        };
    }

    private readonly Dictionary<string, Term> _terms = [];
    private int _unknowns;

    private Term T(string kind, long num = 0, string? text = null, params Term[] args)
    {
        var key = $"{kind}|{num}|{text}|{string.Join(",", args.Select(a => a.Id))}";
        if (!_terms.TryGetValue(key, out var term))
            _terms[key] = term = new Term { Id = _terms.Count, Kind = kind, Num = num, Text = text, Args = args };
        return term;
    }

    private Term VregInput(int vreg) => Input($"v{vreg}");

    private Term Const(long value) => T("c", value & 0xFF);
    private Term Input(string name) => T("in", 0, name);
    private Term Unknown() => T("unk", ++_unknowns);
    private static bool IsConst(Term t, long value) => t.Kind == "c" && t.Num == (value & 0xFF);

    private (Term, Term) Ordered(Term a, Term b) => a.Id <= b.Id ? (a, b) : (b, a);

    private Term Adc(Term a, Term b, Term c) { var (x, y) = Ordered(a, b); return T("adc", 0, null, x, y, c); }
    private Term AdcC(Term a, Term b, Term c) { var (x, y) = Ordered(a, b); return T("adcc", 0, null, x, y, c); }
    private Term Sbc(Term a, Term b, Term c) => T("sbc", 0, null, a, b, c);
    private Term SbcC(Term a, Term b, Term c) => T("sbcc", 0, null, a, b, c);
    private Term Logic(string kind, Term a, Term b) { var (x, y) = Ordered(a, b); return T(kind, 0, null, x, y); }
    private Term Cmp(Term a, Term b) => T("nzcmp", 0, null, a, b);
    private Term CmpC(Term a, Term b) => T("cmpc", 0, null, a, b);
    private Term NzValue(Term t) => T("nzval", 0, null, t);

    // A carry stored as a byte is 0 or 1; shifting that byte back recovers it.
    private Term Shl(Term a, Term cin)
    {
        if (IsConst(a, 0)) return cin.Kind == "c" ? Const(cin.Num) : T("tobyte", 0, null, cin);
        return T("shl", 0, null, a, cin);
    }

    private Term ShlC(Term a, Term cin) => IsConst(a, 0) ? Const(0) : T("shlc", 0, null, a, cin);

    private Term Lshr(Term a, Term cin) => a.Kind == "tobyte" && IsConst(cin, 0) ? Const(0) : T("lshr", 0, null, a, cin);

    private Term LshrC(Term a, Term cin)
    {
        if (a.Kind == "tobyte") return a.Args[0];
        if (a.Kind == "c") return Const(a.Num & 1);
        return T("lshrc", 0, null, a, cin);
    }

    private Term Load(Term address, int version) => T("load", version, null, address);
    private Term Address(string symbol, long offset) => T("addr", offset, symbol);
    private Term Pointer(Term lo, Term hi, Term offset) => T("ptr", 0, null, lo, hi, offset);
    private Term SymbolByte(string symbol, long half) => T("sym", half, symbol);
    private Term Returned(int call, int register) => T("ret", call, RegisterName(register));

    // A call goes to a function or to a fixed address (an OS routine).
    private static string CalleeName(MirOperand operand) =>
        operand is Symbol symbol ? symbol.Name : $"${((Immediate)operand).Value:X}";

    private static string RegisterName(int register) => MOS6502Registers.NameOf(register);

    // ------------------------------------------------------------------
    // What the block should do
    // ------------------------------------------------------------------

    private enum EventKind { Store, Call, Branch, Jump, Return }

    private sealed class Event
    {
        public required EventKind Kind;
        public Term? Address, Value;                 // Store
        public string? Callee;                       // Call
        public Dictionary<int, Term> Registers = []; // Call arguments, Return values
        public CmpBrCondition Condition;             // Branch
        public Term? Left, Right;                    // Branch
        public MirBlock? Target;                     // Branch, Jump
        public Dictionary<int, Term> Homes = [];     // Branch, Jump: vreg -> term its home must hold
        public string Describe = string.Empty;
    }

    private readonly List<Event> _events = [];
    private MirFunction _function = null!;
    private MirBlock _block = null!;

    public void Check(MirBlock block, List<MirInstruction> before, List<MirInstruction> after)
    {
        _function = placer.Function;
        _block = block;
        _events.Clear();

        // Hand-written code is passed through as it is: nothing to check against.
        if (!before.Any(i => i.Operands.Any(o => o is VirtualReg || o is BlockTarget { Args.Length: > 0 })))
            return;

        if (!TryBuildExpectation(before)) return;
        Simulate(after);
    }

    private Term Get(MirOperand operand, Dictionary<int, Term> env)
    {
        switch (operand)
        {
            case Immediate imm:
                return Const(imm.Value);
            case VirtualReg { IsDefinition: false } use:
                if (placer._constants.TryGetValue(use.Id, out var constant)) return Const(constant);
                if (placer._remat.TryGetValue(use.Id, out var remat)) return SymbolByte(remat.Symbol.Name, remat.Half);
                return env.TryGetValue(use.Id, out var term) ? term : VregInput(use.Id);
        }
        throw new InvalidOperationException($"PlacementChecker: operand {operand}.");
    }

    // The value a vreg has just before an exit: its definition in this block, else
    // what it came in with.
    private Dictionary<int, Term> HomesFor(MirBlock target, MirOperand[] args, Dictionary<int, Term> env)
    {
        var homes = new Dictionary<int, Term>();
        foreach (var vreg in placer._liveIn[target])
            homes[vreg] = Get(new VirtualReg(vreg, IsDefinition: false), env);
        for (var k = 0; k < target.Parameters.Count; k++)
        {
            var parameter = target.Parameters[k];
            if (placer._uses.GetValueOrDefault(parameter) == 0) continue;
            homes[parameter] = Get(args[k], env);
        }
        return homes;
    }

    // A block with an operation the checker does not model is left unchecked.
    private static bool Skip(MirInstruction instr) => false;

    private bool TryBuildExpectation(List<MirInstruction> before)
    {
        var env = new Dictionary<int, Term>();
        var expected = new Dictionary<int, Term>();    // fixed registers set by copies since the last call
        var calls = 0;
        var version = 0;

        Term Physical(int register) => calls == 0 ? Input(RegisterName(register)) : Returned(calls, register);

        foreach (var instr in before)
        {
            var o = instr.Operands;
            if (instr.Opcode.Dialect == PseudoDialect.Id)
            {
                switch ((PseudoOp)instr.Opcode.Code)
                {
                    case PseudoOp.Copy:
                        if (o[0] is PhysicalReg to)
                            expected[to.Id] = Get(o[1], env);
                        else if (o[1] is PhysicalReg from)
                            env[((VirtualReg)o[0]).Id] = Physical(from.Id);
                        else
                            env[((VirtualReg)o[0]).Id] = Get(o[1], env);
                        continue;
                    case PseudoOp.Merge:
                        continue;
                }
                return Skip(instr);
            }
            if (instr.Opcode.Dialect == MemDialect.Id && (MemOp)instr.Opcode.Code == MemOp.Symbol)
                continue;

            if (instr.Opcode.Dialect == CfDialect.Id && (CfOp)instr.Opcode.Code == CfOp.Br)
            {
                var target = (BlockTarget)o[0];
                _events.Add(new Event
                {
                    Kind = EventKind.Jump, Target = target.Block, Homes = HomesFor(target.Block, target.Args, env),
                    Describe = $"jump to {B(target.Block)}",
                });
                continue;
            }

            if (instr.Opcode.Dialect != MOS6502Dialect.Id) return Skip(instr);
            switch ((MOS6502Op)instr.Opcode.Code)
            {
                case MOS6502Op.Const:
                    if (o[1] is Immediate imm) env[((VirtualReg)o[0]).Id] = Const(imm.Value);
                    else env[((VirtualReg)o[0]).Id] = SymbolByte(((Symbol)o[1]).Name, ((Immediate)o[2]).Value);
                    break;
                case MOS6502Op.Adc:
                    env[Id(o[0])] = Adc(Get(o[2], env), Get(o[3], env), Get(o[4], env));
                    env[Id(o[1])] = AdcC(Get(o[2], env), Get(o[3], env), Get(o[4], env));
                    break;
                case MOS6502Op.Sbc:
                    env[Id(o[0])] = Sbc(Get(o[2], env), Get(o[3], env), Get(o[4], env));
                    env[Id(o[1])] = SbcC(Get(o[2], env), Get(o[3], env), Get(o[4], env));
                    break;
                case MOS6502Op.Inc:
                    env[Id(o[0])] = Adc(Get(o[1], env), Const(1), Const(0));
                    break;
                case MOS6502Op.Dec:
                    env[Id(o[0])] = Sbc(Get(o[1], env), Const(1), Const(1));
                    break;
                case MOS6502Op.And:
                    env[Id(o[0])] = Logic("and", Get(o[1], env), Get(o[2], env));
                    break;
                case MOS6502Op.Ora:
                    env[Id(o[0])] = Logic("ora", Get(o[1], env), Get(o[2], env));
                    break;
                case MOS6502Op.Eor:
                    env[Id(o[0])] = Logic("eor", Get(o[1], env), Get(o[2], env));
                    break;
                case MOS6502Op.Shle:
                    env[Id(o[0])] = Shl(Get(o[2], env), Get(o[3], env));
                    env[Id(o[1])] = ShlC(Get(o[2], env), Get(o[3], env));
                    break;
                case MOS6502Op.Lshre:
                    env[Id(o[0])] = Lshr(Get(o[2], env), Get(o[3], env));
                    env[Id(o[1])] = LshrC(Get(o[2], env), Get(o[3], env));
                    break;
                case MOS6502Op.Cmp:
                    env[Id(o[0])] = CmpC(Get(o[1], env), Get(o[2], env));
                    break;
                case MOS6502Op.CmpBr:
                {
                    var (a, b) = (Get(o[0], env), Get(o[1], env));
                    for (var k = 2; k < o.Length; k += 2)
                    {
                        var target = (BlockTarget)o[k + 1];
                        _events.Add(new Event
                        {
                            Kind = EventKind.Branch, Condition = (CmpBrCondition)((Immediate)o[k]).Value,
                            Left = a, Right = b, Target = target.Block,
                            Homes = HomesFor(target.Block, target.Args, env),
                            Describe = $"branch {CmpBrConditionNames.ToText((CmpBrCondition)((Immediate)o[k]).Value)} to {B(target.Block)}",
                        });
                    }
                    break;
                }
                case MOS6502Op.Load:
                    env[Id(o[0])] = o[1] is Symbol loadSymbol
                        ? Load(Address(loadSymbol.Name, loadSymbol.Offset), version)
                        : Load(Pointer(Get(o[1], env), Get(o[2], env), Const(((Immediate)o[3]).Value)), version);
                    break;
                case MOS6502Op.Store:
                {
                    var address = o[1] is Symbol storeSymbol
                        ? Address(storeSymbol.Name, storeSymbol.Offset)
                        : Pointer(Get(o[1], env), Get(o[2], env), Const(((Immediate)o[3]).Value));
                    _events.Add(new Event { Kind = EventKind.Store, Address = address, Value = Get(o[0], env), Describe = "store" });
                    version++;
                    break;
                }
                case MOS6502Op.FrameLoadByte:
                    env[Id(o[0])] = Load(Address(((Symbol)o[1]).Name, ((Immediate)o[2]).Value), version);
                    break;
                case MOS6502Op.FrameStoreByte:
                    _events.Add(new Event
                    {
                        Kind = EventKind.Store, Address = Address(((Symbol)o[0]).Name, ((Immediate)o[1]).Value),
                        Value = Get(o[2], env), Describe = "frame store",
                    });
                    version++;
                    break;
                case MOS6502Op.JsrAbs:
                    _events.Add(new Event
                    {
                        Kind = EventKind.Call, Callee = CalleeName(o[0]), Describe = $"call {CalleeName(o[0])}",
                        Registers = new Dictionary<int, Term>(expected),
                    });
                    expected.Clear();
                    calls++;
                    version++;
                    break;
                case MOS6502Op.Rts:
                    _events.Add(new Event
                    {
                        Kind = EventKind.Return, Describe = "return",
                        Registers = new Dictionary<int, Term>(expected),
                    });
                    break;
                default:
                    return Skip(instr);
            }
        }
        return true;

        static int Id(MirOperand operand) => ((VirtualReg)operand).Id;
    }

    // ------------------------------------------------------------------
    // What the emitted code does
    // ------------------------------------------------------------------

    private sealed class State
    {
        public Term[] R = new Term[3];                      // A, X, Y
        public Term C = null!;
        public Term Nz = null!;
        public Dictionary<int, Term> Memory = [];            // zero-page registers and homes
        public int Version;
        public int Calls;

        public State Clone() => new()
        {
            R = (Term[])R.Clone(), C = C, Nz = Nz, Memory = new Dictionary<int, Term>(Memory),
            Version = Version, Calls = Calls,
        };
    }

    private int _next;

    private string B(MirBlock block) => $"bb{_function.Blocks.IndexOf(block)}";

    private void Fail(string message) =>
        throw new MirVerificationException(
            $"PlacementChecker: in @{_function.Name}, {B(_block)}: {message}"
);

    private void Simulate(List<MirInstruction> after)
    {
        var state = placer.Contract(_block) is { } contract ? Inherit(contract) : ConventionalEntry();

        _next = 0;
        if (!Execute(after, state, edgeOf: null))
            Fail("the code does not end in a jump or a return.");
        if (_next != _events.Count)
            Fail($"the code leaves out {_events.Count - _next} of the block's effects, the first being a {_events[_next].Describe}.");
    }

    // The state a block that starts from a snapshot starts in: the registers and the
    // homes the snapshot names hold the inputs they are named for; the code of every
    // edge into the block is checked to deliver exactly that (CheckContract).
    private State Inherit(Snapshot contract)
    {
        const int ImmBase = Snapshot.ImmBase;

        // Only what is live into the block counts; a register may hold a dead value.
        bool Wanted(int name) =>
            name >= ImmBase || placer._liveIn[_block].Contains(name) || _block.Parameters.Contains(name);
        Term? Of(int name) =>
            name == Snapshot.Empty || !Wanted(name) ? null
            : name >= ImmBase ? Const(name - ImmBase)
            : placer._remat.TryGetValue(name, out var remat) ? SymbolByte(remat.Symbol.Name, remat.Half)
            : VregInput(name);

        var state = new State { C = Of(contract.C) ?? Unknown(), Nz = Unknown() };
        for (var k = 0; k < 3; k++)
            state.R[k] = Of(contract.R[k]) ?? Unknown();
        if (Of(contract.PtrLo) is { } lo) state.Memory[MOS6502Variants.PointerLo] = lo;
        if (Of(contract.PtrHi) is { } hi) state.Memory[MOS6502Variants.PointerHi] = hi;
        foreach (var vreg in contract.Homed.Where(Wanted))
            if (placer.TryGetHome(vreg, out var home))
                state.Memory[home] = placer._remat.TryGetValue(vreg, out var remat)
                    ? SymbolByte(remat.Symbol.Name, remat.Half)
                    : VregInput(vreg);
        return state;
    }

    private State ConventionalEntry()
    {
        var state = new State { C = Unknown(), Nz = Unknown() };
        var entry = _function.Blocks[0] == _block;
        state.R[A] = entry ? Input(RegisterName(A)) : Unknown();
        state.R[X] = entry ? Input(RegisterName(X)) : Unknown();
        state.R[Y] = entry ? Input(RegisterName(Y)) : Unknown();

        if (entry)
            for (var n = 2; n < 20; n++)
                state.Memory[MOS6502Registers.RC(n)] = Input(RegisterName(MOS6502Registers.RC(n)));
        foreach (var vreg in placer._liveIn[_block].Concat(_block.Parameters))
            if (placer.TryGetHome(vreg, out var home))
                state.Memory[home] = placer._remat.TryGetValue(vreg, out var remat)
                    ? SymbolByte(remat.Symbol.Name, remat.Half)
                    : VregInput(vreg);
        return state;
    }

    private Event Expect(EventKind kind, string what)
    {
        if (_next >= _events.Count)
            Fail($"the code does a {what} that the block does not.");
        var e = _events[_next];
        if (e.Kind != kind)
            Fail($"the code does a {what} where the block does a {e.Describe}.");
        _next++;
        return e;
    }

    private Term Read(State s, MirOperand operand)
    {
        switch (operand)
        {
            case Immediate imm:
                return Const(imm.Value);
            case Symbol sym:
                return Load(Address(sym.Name, sym.Offset), s.Version);
            case PhysicalReg { Id: A or X or Y } reg:
                return s.R[reg.Id];
            case PhysicalReg reg:
                return s.Memory.TryGetValue(reg.Id, out var term) ? term : Unknown();
        }
        throw new InvalidOperationException($"PlacementChecker: cannot read {operand}.");
    }

    private static int Index(MirOperand operand) => ((PhysicalReg)operand).Id;

    // Runs straight-line code; true if it ended in a jump or return. `edgeOf` is the
    // branch event an edge block is the way out of.
    private bool Execute(IEnumerable<MirInstruction> code, State s, Event? edgeOf)
    {
        foreach (var instr in code)
        {
            if (instr.Opcode.Dialect != MOS6502Dialect.Id)
                Fail($"unexpected {DialectRegistry.ById(instr.Opcode.Dialect).Prefix} op in placed code.");
            var op = (MOS6502Op)instr.Opcode.Code;
            var o = instr.Operands;

            void SetReg(int register, Term value) { s.R[register] = value; s.Nz = NzValue(value); }

            switch (op)
            {
                case MOS6502Op.LdaImm or MOS6502Op.LdaZp or MOS6502Op.LdaAbs:
                    SetReg(A, Read(s, o[1]));
                    break;
                case MOS6502Op.LdxImm or MOS6502Op.LdxZp or MOS6502Op.LdxAbs:
                    SetReg(X, Read(s, o[1]));
                    break;
                case MOS6502Op.LdyImm or MOS6502Op.LdyZp or MOS6502Op.LdyAbs:
                    SetReg(Y, Read(s, o[1]));
                    break;
                case MOS6502Op.LdaImmSymLo or MOS6502Op.LdaImmSymHi:
                    SetReg(A, SymbolByte(((Symbol)o[1]).Name, op == MOS6502Op.LdaImmSymLo ? 0 : 1));
                    break;
                case MOS6502Op.LdxImmSymLo or MOS6502Op.LdxImmSymHi:
                    SetReg(X, SymbolByte(((Symbol)o[1]).Name, op == MOS6502Op.LdxImmSymLo ? 0 : 1));
                    break;
                case MOS6502Op.LdaIndY:
                    SetReg(A, Load(Pointer(Read(s, o[1]), Read(s, o[2]), s.R[Y]), s.Version));
                    break;

                case MOS6502Op.StaZp or MOS6502Op.StxZp or MOS6502Op.StyZp:
                    s.Memory[Index(o[0])] = s.R[op == MOS6502Op.StaZp ? A : op == MOS6502Op.StxZp ? X : Y];
                    break;
                case MOS6502Op.StaAbs or MOS6502Op.StxAbs or MOS6502Op.StyAbs:
                {
                    var register = op == MOS6502Op.StaAbs ? A : op == MOS6502Op.StxAbs ? X : Y;
                    var symbol = (Symbol)o[1];
                    CheckStore(Address(symbol.Name, symbol.Offset), s.R[register]);
                    s.Version++;
                    break;
                }
                case MOS6502Op.StaIndY:
                    CheckStore(Pointer(Read(s, o[0]), Read(s, o[2]), s.R[Y]), s.R[A]);
                    s.Version++;
                    break;

                case MOS6502Op.Tax: SetReg(X, s.R[A]); break;
                case MOS6502Op.Tay: SetReg(Y, s.R[A]); break;
                case MOS6502Op.Txa: SetReg(A, s.R[X]); break;
                case MOS6502Op.Tya: SetReg(A, s.R[Y]); break;

                case MOS6502Op.AdcImm or MOS6502Op.AdcZp or MOS6502Op.AdcAbs:
                {
                    var (a, m, c) = (s.R[A], Read(s, o[3]), s.C);
                    s.C = AdcC(a, m, c);
                    SetReg(A, Adc(a, m, c));
                    break;
                }
                case MOS6502Op.SbcImm or MOS6502Op.SbcZp or MOS6502Op.SbcAbs:
                {
                    var (a, m, c) = (s.R[A], Read(s, o[3]), s.C);
                    s.C = SbcC(a, m, c);
                    SetReg(A, Sbc(a, m, c));
                    break;
                }
                case MOS6502Op.AndImm or MOS6502Op.AndZp or MOS6502Op.AndAbs:
                    SetReg(A, Logic("and", s.R[A], Read(s, o[2])));
                    break;
                case MOS6502Op.OraImm or MOS6502Op.OraZp or MOS6502Op.OraAbs:
                    SetReg(A, Logic("ora", s.R[A], Read(s, o[2])));
                    break;
                case MOS6502Op.EorImm or MOS6502Op.EorZp or MOS6502Op.EorAbs:
                    SetReg(A, Logic("eor", s.R[A], Read(s, o[2])));
                    break;

                case MOS6502Op.CmpImm or MOS6502Op.CmpZp or MOS6502Op.CmpAbs:
                    Compare(s, A, o[1]);
                    break;
                case MOS6502Op.CpxImm or MOS6502Op.CpxZp or MOS6502Op.CpxAbs:
                    Compare(s, X, o[1]);
                    break;
                case MOS6502Op.CpyImm or MOS6502Op.CpyZp or MOS6502Op.CpyAbs:
                    Compare(s, Y, o[1]);
                    break;

                case MOS6502Op.AslAcc or MOS6502Op.RolAcc:
                {
                    var cin = op == MOS6502Op.AslAcc ? Const(0) : s.C;
                    var a = s.R[A];
                    s.C = ShlC(a, cin);
                    SetReg(A, Shl(a, cin));
                    break;
                }
                case MOS6502Op.LsrAcc or MOS6502Op.RorAcc:
                {
                    var cin = op == MOS6502Op.LsrAcc ? Const(0) : s.C;
                    var a = s.R[A];
                    s.C = LshrC(a, cin);
                    SetReg(A, Lshr(a, cin));
                    break;
                }
                case MOS6502Op.Clc: s.C = Const(0); break;
                case MOS6502Op.Sec: s.C = Const(1); break;

                case MOS6502Op.Inx: SetReg(X, Adc(s.R[X], Const(1), Const(0))); break;
                case MOS6502Op.Iny: SetReg(Y, Adc(s.R[Y], Const(1), Const(0))); break;
                case MOS6502Op.Dex: SetReg(X, Sbc(s.R[X], Const(1), Const(1))); break;
                case MOS6502Op.Dey: SetReg(Y, Sbc(s.R[Y], Const(1), Const(1))); break;

                case MOS6502Op.FrameLoadByte:
                    SetReg(A, Load(Address(((Symbol)o[1]).Name, ((Immediate)o[2]).Value), s.Version));
                    ClobberScratch(s, includeX: false);
                    break;
                case MOS6502Op.FrameStoreByte:
                    CheckStore(Address(((Symbol)o[0]).Name, ((Immediate)o[1]).Value), s.R[A]);
                    s.Version++;
                    ClobberScratch(s, includeX: true);
                    break;

                case MOS6502Op.Beq or MOS6502Op.Bne or MOS6502Op.Bcs or MOS6502Op.Bcc or MOS6502Op.Bmi or MOS6502Op.Bpl:
                    CheckBranch(s, op, (BlockTarget)o[0]);
                    break;

                case MOS6502Op.JmpAbs:
                {
                    var target = ((BlockTarget)o[0]).Block;
                    if (edgeOf is not null)
                    {
                        if (target != edgeOf.Target)
                            Fail($"an edge block jumps to {B(target)} instead of {B(edgeOf.Target!)}.");
                        CheckHomes(s, edgeOf);
                    }
                    else
                    {
                        var jump = Expect(EventKind.Jump, $"jump to {B(target)}");
                        if (jump.Target != target)
                            Fail($"the code jumps to {B(target)} where the block jumps to {B(jump.Target!)}.");
                        CheckHomes(s, jump);
                    }
                    return true;
                }
                case MOS6502Op.JsrAbs:
                {
                    var symbol = CalleeName(o[0]);
                    var call = Expect(EventKind.Call, $"call {symbol}");
                    if (call.Callee != symbol)
                        Fail($"the code calls {symbol} where the block calls {call.Callee}.");
                    foreach (var (register, term) in call.Registers)
                    {
                        var actual = register is A or X or Y ? s.R[register]
                            : s.Memory.TryGetValue(register, out var m) ? m : Unknown();
                        if (actual != term)
                            Fail($"call {symbol}: ${RegisterName(register)} holds {actual}, expected {term}.");
                    }
                    s.Calls++;
                    s.Version++;
                    for (var r = 0; r < 3; r++) s.R[r] = Returned(s.Calls, r);
                    s.C = Unknown();
                    s.Nz = Unknown();
                    for (var n = 2; n < 20; n++)
                        s.Memory[MOS6502Registers.RC(n)] = Returned(s.Calls, MOS6502Registers.RC(n));
                    break;
                }
                case MOS6502Op.Rts:
                {
                    var ret = Expect(EventKind.Return, "return");
                    foreach (var (register, term) in ret.Registers)
                    {
                        var actual = register is A or X or Y ? s.R[register]
                            : s.Memory.TryGetValue(register, out var m) ? m : Unknown();
                        if (actual != term)
                            Fail($"return: ${RegisterName(register)} holds {actual}, expected {term}.");
                    }
                    return true;
                }
                default:
                    Fail($"the checker does not model {DialectRegistry.ById(MOS6502Dialect.Id).GetOpName(instr.Opcode.Code)}.");
                    break;
            }
        }
        return false;
    }

    private void Compare(State s, int register, MirOperand memory)
    {
        var (a, b) = (s.R[register], Read(s, memory));
        s.Nz = Cmp(a, b);
        s.C = CmpC(a, b);
    }

    private void ClobberScratch(State s, bool includeX)
    {
        s.R[Y] = Unknown();
        if (includeX) s.R[X] = Unknown();
        s.Memory.Remove(MOS6502Variants.PointerLo);
        s.Memory.Remove(MOS6502Variants.PointerHi);
    }

    private void CheckStore(Term address, Term value)
    {
        var store = Expect(EventKind.Store, "store");
        if (store.Address != address)
            Fail($"a store goes to {address} where the block stores to {store.Address}.");
        if (store.Value != value)
            Fail($"a store to {address} writes {value} where the block writes {store.Value}.");
    }

    private void CheckBranch(State s, MOS6502Op op, BlockTarget target)
    {
        var condition = op switch
        {
            MOS6502Op.Beq => CmpBrCondition.Eq,
            MOS6502Op.Bne => CmpBrCondition.Ne,
            MOS6502Op.Bcs => CmpBrCondition.Cs,
            MOS6502Op.Bcc => CmpBrCondition.Cc,
            MOS6502Op.Bmi => CmpBrCondition.Mi,
            _ => CmpBrCondition.Pl,
        };

        Term a, b;
        if (condition is CmpBrCondition.Cs or CmpBrCondition.Cc)
        {
            if (s.C.Kind != "cmpc") { Fail($"a branch on the carry, which holds {s.C}."); return; }
            (a, b) = (s.C.Args[0], s.C.Args[1]);
        }
        else if (s.Nz.Kind == "nzcmp")
            (a, b) = (s.Nz.Args[0], s.Nz.Args[1]);
        else if (s.Nz.Kind == "nzval")
            (a, b) = (s.Nz.Args[0], Const(0));
        else { Fail($"a branch on N / Z, which hold {s.Nz}."); return; }

        var branch = Expect(EventKind.Branch, $"branch to {B(target.Block)}");
        if (branch.Condition != condition || branch.Left != a || branch.Right != b)
            Fail($"a branch {CmpBrConditionNames.ToText(condition)} on ({a}, {b}) where the block has " +
                 $"{CmpBrConditionNames.ToText(branch.Condition)} on ({branch.Left}, {branch.Right}).");

        if (placer._edgeBlocks.Contains(target.Block))
        {
            var edge = s.Clone();
            if (!Execute(target.Block.Instructions, edge, branch))
                Fail("an edge block does not end in a jump.");
        }
        else
        {
            if (target.Block != branch.Target)
                Fail($"a branch to {B(target.Block)} where the block branches to {B(branch.Target!)}.");
            CheckHomes(s, branch);
        }
    }

    // Whether `term` is the input value of a vreg that has the same home as `vreg`.
    private bool SharesHome(Term term, int vreg) =>
        term.Kind == "in" && term.Text is { Length: > 1 } text && text[0] == 'v'
        && int.TryParse(text.AsSpan(1), out var other) && placer.HomeKey(other) == placer.HomeKey(vreg);

    // On an exit, the homes hold what the successor expects.
    //
    // A successor that starts from a snapshot (ExitRole) takes the registers as they
    // are: the first edge into it records its state for the block's own check, and
    // every edge into it, the first included, is checked against the snapshot the
    // block starts from.
    private void CheckContract(State s, Event exit, Snapshot contract)
    {
        const int ImmBase = Snapshot.ImmBase;
        for (var k = 0; k < 3; k++)
        {
            var name = contract.R[k];
            if (name == Snapshot.Empty) continue;
            Term expected;
            if (name >= ImmBase) expected = Const(name - ImmBase);
            else if (!exit.Homes.TryGetValue(name, out expected!)) continue;   // a dead value the registers happen to hold
            if (s.R[k] != expected)
                Fail($"{exit.Describe}: ${RegisterName(k)} holds {s.R[k]}, the successor expects {expected} (v{name}).");
        }

        foreach (var name in contract.Homed)
        {
            if (!exit.Homes.TryGetValue(name, out var term) || placer._remat.ContainsKey(name)) continue;
            if (!placer.TryGetHome(name, out var home))
            {
                if (term != VregInput(name) && !SharesHome(term, name))
                    Fail($"{exit.Describe}: v{name} is expected in its home but was never stored (it is {term}).");
                continue;
            }
            var actual = s.Memory.TryGetValue(home, out var held) ? held : Unknown();
            if (actual != term)
                Fail($"{exit.Describe}: the home ${RegisterName(home)} of v{name} holds {actual}, expected {term}.");
        }
    }

    private void CheckHomes(State s, Event exit)
    {
        var role = placer.RoleOf(_block, exit.Target!);
        if (role != ExitRole.Conventional)
        {
            if (placer.Contract(exit.Target!) is { } contract)
                CheckContract(s, exit, contract);
            return;
        }
        foreach (var (vreg, term) in exit.Homes)
        {
            if (!placer.TryGetHome(vreg, out var home))
            {
                // No code has touched the home yet, so it still holds what the vreg it
                // shares with came in as (a branch whose argument is such a vreg).
                if (term != VregInput(vreg) && !placer._remat.ContainsKey(vreg) && !SharesHome(term, vreg))
                    Fail($"v{vreg} is needed in {B(exit.Target!)} but was never stored (it is {term}).");
                continue;
            }
            var actual = s.Memory.TryGetValue(home, out var held) ? held : Unknown();
            if (actual != term)
                Fail($"{exit.Describe}: the home ${RegisterName(home)} of v{vreg} holds {actual}, expected {term}.");
        }
    }
}
