using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Knobs of the placement search. The objective of a placement is
//   bytes * SizeWeight + cycles * SpeedWeight * (static block frequency)
// summed over the emitted instructions, so a loop body pays for its cycles and
// straight-line code mostly for its size.
public sealed record BeamPlacementOptions
{
    // States kept per operation; 1 is a greedy placement.
    public int BeamWidth { get; init; } = 8;
    public int SizeWeight { get; init; } = 1;
    public int SpeedWeight { get; init; } = 1;
}

// Placement within blocks: a beam search over what A, X, Y, the
// carry and the N/Z flags hold at every point of a block, choosing for each
// operation the variant of MOS6502Variants that is cheapest given that state, and
// when a value is stored to or reloaded from its home.
//
// The search state is the contents of A, X, Y, C, the value N/Z reflect, the
// scratch pointer pair, and the set of values whose home holds their value. For
// each operation, each way of implementing it (a variant with its operands in
// particular registers) turns a state into a successor by a fixed sequence of
// moves: save what the operation overwrites, put the operands where the variant
// wants them, emit the variant. A value is stored to its home lazily, when
// something is about to overwrite its only copy, so a value that stays in a
// register until its last use is never stored.
//
// Across blocks nothing is carried, as in the naive placement: every value live
// across a boundary is in its home and the registers are empty. The search runs on
// the stretches of operations between two barriers (a call, a frame access,
// hand-written runtime code), including the runs of copies that set up a call or a
// return and the jump that ends a block; a stretch has to end with every value the
// rest of the block, or a successor, still needs in its home. Barriers themselves
// are placed exactly as the naive placement does.
public sealed class MOS6502BeamPlacementPass(BeamPlacementOptions? options = null) : MirFunctionPass
{
    public override string Name => "MOS6502BeamPlacement";

    public override void Run(MirFunction function) =>
        new BeamPlacer(function, options ?? new BeamPlacementOptions()).Run();
}

internal sealed class BeamPlacer(MirFunction function, BeamPlacementOptions options)
    : MOS6502NaivePlacementPass.Placer(function)
{
    // What a register, the carry, the N/Z flags or a pointer byte holds: nothing
    // known, a value of the block (its dense index), or a known byte.
    private const int Empty = -1;
    private const int ImmBase = 1 << 20;
    private static int ImmContent(long value) => ImmBase + (int)(value & 0xFF);
    private static bool IsImm(int content) => content >= ImmBase;
    private static long ImmValue(int content) => content - ImmBase;

    private LoopInfo _loops = null!;

    internal BeamPlacementOptions Options => options;

    // ------------------------------------------------------------------
    // Liveness
    // ------------------------------------------------------------------

    // Arguments that arrive in zero-page registers stay there for the whole
    // function when nothing in it can overwrite them: no call (a callee may use
    // the registers) and no copy into a fixed zero-page register.
    private readonly Dictionary<int, int> _argRegister = [];

    internal override PhysicalReg Home(int vreg) =>
        _argRegister.TryGetValue(vreg, out var register)
            ? new PhysicalReg(register, IsDefinition: false)
            : base.Home(vreg);

    internal override bool TryGetHome(int vreg, out int register)
    {
        if (_argRegister.TryGetValue(vreg, out register)) return true;
        return base.TryGetHome(vreg, out register);
    }

    // Values that are never live together and are linked by a block parameter or a
    // copy share a home: the representative of each such class.
    private Dictionary<int, int> _homeClass = [];

    internal override int HomeKey(int vreg) => _homeClass.GetValueOrDefault(vreg, vreg);

    private bool ArgumentsStayInPlace()
    {
        foreach (var block in Function.Blocks)
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode.Dialect == PseudoDialect.Id)
                {
                    if ((PseudoOp)instr.Opcode.Code == PseudoOp.Copy
                        && instr.Operands[0] is PhysicalReg { Id: >= 0 } to
                        && to.Id is not (A or X or Y))
                        return false;
                    continue;
                }
                if (instr.Opcode.Dialect == MOS6502Dialect.Id
                    && (MOS6502Op)instr.Opcode.Code is MOS6502Op.JsrAbs or MOS6502Op.FrameLoadByte or MOS6502Op.FrameStoreByte)
                    return false;
            }
        return true;
    }

    internal override void BeforePlacement()
    {
        if (ArgumentsStayInPlace())
            foreach (var block in Function.Blocks)
                foreach (var instr in block.Instructions)
                    if (IsCopyFromZeroPage(instr) && instr.Operands[0] is VirtualReg d && !Unused(d))
                        _argRegister[d.Id] = ((PhysicalReg)instr.Operands[1]).Id;

        _loops = new LoopAnalysis().Compute(Function);
        _homeClass = HomeClasses.Build(this, _loops, vreg => _argRegister.ContainsKey(vreg) || _remat.ContainsKey(vreg));
    }

    // ------------------------------------------------------------------
    // One block
    // ------------------------------------------------------------------

    // Per-block tables: dense indices for the values the block touches, where each
    // is defined and last used.
    private sealed class BlockInfo
    {
        public required List<MirInstruction> Instrs;
        public required Dictionary<int, int> Index;
        public required List<int> Vreg;
        public required int[] DefPos;
        public required int[] LastUse;
        public required long Frequency;
        // The zero-page register a value arrived in (an argument), or -1.
        public required int[] FixedReg;
        public int Count => Vreg.Count;
    }

    private BlockInfo Analyse(MirBlock block)
    {
        var instrs = block.Instructions.ToList();
        var index = new Dictionary<int, int>();
        var vregs = new List<int>();
        var defPos = new List<int>();
        var lastUse = new List<int>();

        int Touch(int vreg, int pos, bool isDef)
        {
            if (!index.TryGetValue(vreg, out var i))
            {
                i = vregs.Count;
                index[vreg] = i;
                vregs.Add(vreg);
                defPos.Add(-1);
                lastUse.Add(-1);
            }
            if (isDef) defPos[i] = pos;
            lastUse[i] = Math.Max(lastUse[i], pos);
            return i;
        }

        foreach (var parameter in block.Parameters.Where(NeedsHome))
            Touch(parameter, -1, isDef: true);
        for (var pos = 0; pos < instrs.Count; pos++)
        {
            foreach (var use in Uses(instrs[pos])) Touch(use.Id, pos, isDef: false);
            foreach (var def in Defs(instrs[pos])) Touch(def.Id, pos, isDef: true);
        }
        foreach (var vreg in _liveOut[block])
            if (index.TryGetValue(vreg, out var i)) lastUse[i] = int.MaxValue;

        var fixedReg = Enumerable.Repeat(-1, vregs.Count).ToArray();
        foreach (var instr in instrs)
            if (IsCopyFromZeroPage(instr) && instr.Operands[0] is VirtualReg d && index.TryGetValue(d.Id, out var di))
                fixedReg[di] = ((PhysicalReg)instr.Operands[1]).Id;

        return new BlockInfo
        {
            Instrs = instrs,
            Index = index,
            Vreg = vregs,
            DefPos = [.. defPos],
            LastUse = [.. lastUse],
            Frequency = _loops.Frequency(block),
            FixedReg = fixedReg,
        };
    }

    internal override void PlaceBlock(MirBlock block)
    {
        _out = [];
        _carryHolds = -1;

        var info = Analyse(block);
        var instrs = info.Instrs;
        for (var i = 0; i < instrs.Count;)
        {
            if (IsSearchable(instrs[i]))
            {
                var start = i;
                while (i < instrs.Count && IsSearchable(instrs[i])) i++;
                RunSegment(info, start, i);
            }
            else
            {
                i = PlaceInstruction(instrs, i);
            }
        }

        CheckBlock(block, instrs);
        block.Instructions.Clear();
        foreach (var instr in _out)
        {
            instr.Parent = block;
            block.Instructions.Add(instr);
        }
    }

    private static bool IsCopy(MirInstruction instr) =>
        instr.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;

    // `%v = copy $rcN`: an argument that arrives in a zero-page register, which is
    // then also the home of the value until something overwrites it.
    private static bool IsCopyFromZeroPage(MirInstruction instr) =>
        IsCopy(instr) && instr.Operands[0] is VirtualReg && instr.Operands[1] is PhysicalReg source
        && source.Id >= MOS6502Registers.RC(2) && !MOS6502Registers.IsHome(source.Id)
        && source.Id != MOS6502Variants.PointerLo && source.Id != MOS6502Variants.PointerHi;

    // Dead merges and address ops that take no code.
    private bool IsNoCode(MirInstruction instr) =>
        (instr.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)instr.Opcode.Code == PseudoOp.Merge && Unused(instr.Operands[0]))
        || (instr.Opcode.Dialect == MemDialect.Id && (MemOp)instr.Opcode.Code == MemOp.Symbol && Unused(instr.Operands[0]));

    // The operations the search places; everything else is a barrier.
    private static bool IsJump(MirInstruction instr) =>
        instr.Opcode.Dialect == CfDialect.Id && (CfOp)instr.Opcode.Code == CfOp.Br;

    private bool IsSearchable(MirInstruction instr)
    {
        if (IsCopy(instr) || IsNoCode(instr) || IsJump(instr)) return true;
        if (instr.Opcode.Dialect != MOS6502Dialect.Id) return false;
        switch ((MOS6502Op)instr.Opcode.Code)
        {
            case MOS6502Op.Const:
            case MOS6502Op.Adc:
            case MOS6502Op.Sbc:
            case MOS6502Op.Inc:
            case MOS6502Op.Dec:
            case MOS6502Op.And:
            case MOS6502Op.Ora:
            case MOS6502Op.Eor:
            case MOS6502Op.Shle:
            case MOS6502Op.Lshre:
            case MOS6502Op.Cmp:
            case MOS6502Op.CmpBr:
                return true;
            case MOS6502Op.Load:
            case MOS6502Op.Store:
                // A frame slot is accessed by an op that clobbers A, Y and the
                // pointer pair: a barrier.
                return !(instr.Operands.Any(o => o is Symbol s && IsFrameSlot(s.Name)));
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // The search state
    // ------------------------------------------------------------------

    private sealed class Step
    {
        public Variant? Variant;
        public MemRef Mem;
        public Action<BeamPlacer>? Action;
    }

    private enum MemKind { None, Imm, Symbol, Home, Phys }

    // The memory-side operand of a step, resolved when the step is emitted (a home
    // gets its number then, so only the placement that is kept uses up homes).
    private readonly record struct MemRef(MemKind Kind, long Number, Symbol? Symbol)
    {
        public static MemRef None => new(MemKind.None, 0, null);
        public static MemRef Imm(long value) => new(MemKind.Imm, value, null);
        public static MemRef Sym(Symbol symbol) => new(MemKind.Symbol, 0, symbol);
        public static MemRef HomeOf(int vreg) => new(MemKind.Home, vreg, null);
        public static MemRef Phys(int reg) => new(MemKind.Phys, reg, null);
    }

    private MirOperand? Resolve(MemRef mem) => mem.Kind switch
    {
        MemKind.None => null,
        MemKind.Imm => new Immediate(mem.Number),
        MemKind.Symbol => mem.Symbol,
        MemKind.Home => Home((int)mem.Number),
        MemKind.Phys => new PhysicalReg((int)mem.Number, IsDefinition: false),
        _ => throw new InvalidOperationException(),
    };

    private sealed class State
    {
        public int[] R = [Empty, Empty, Empty];   // A, X, Y
        public int C = Empty;
        public int Nz = Empty;
        public int PtrLo = Empty;
        public int PtrHi = Empty;
        public ulong[] H = [];
        public ulong[] F = [];
        public long Cost;
        public State? Prev;
        public Step[] Steps = [];

        public bool SameLocations(State other) =>
            R[0] == other.R[0] && R[1] == other.R[1] && R[2] == other.R[2]
            && C == other.C && Nz == other.Nz && PtrLo == other.PtrLo && PtrHi == other.PtrHi
            && H.AsSpan().SequenceEqual(other.H) && F.AsSpan().SequenceEqual(other.F);

        public int LocationHash()
        {
            var hash = new HashCode();
            hash.Add(R[0]); hash.Add(R[1]); hash.Add(R[2]);
            hash.Add(C); hash.Add(Nz); hash.Add(PtrLo); hash.Add(PtrHi);
            foreach (var word in H) hash.Add(word);
            foreach (var word in F) hash.Add(word);
            return hash.ToHashCode();
        }
    }

    private sealed class LocationComparer : IEqualityComparer<State>
    {
        public bool Equals(State? a, State? b) => a!.SameLocations(b!);
        public int GetHashCode(State s) => s.LocationHash();
    }

    // The state at the start of the stretch [start, ...): registers empty, every
    // value that is live there in its home.
    private static State InitialState(BlockInfo info, int start)
    {
        var state = new State { H = new ulong[(info.Count + 63) / 64], F = new ulong[(info.Count + 63) / 64] };
        for (var i = 0; i < info.Count; i++)
            if (info.DefPos[i] < start && info.LastUse[i] >= start)
                state.H[i >> 6] |= 1UL << (i & 63);
        return state;
    }

    private void RunSegment(BlockInfo info, int start, int end)
    {
        var beam = new List<State> { InitialState(info, start) };
        for (var pos = start; pos < end;)
        {
            // A run of copies is placed as one parallel move.
            var itemEnd = pos + 1;
            if (IsCopy(info.Instrs[pos]))
                while (itemEnd < end && IsCopy(info.Instrs[itemEnd])) itemEnd++;
            var item = pos;
            pos = itemEnd;

            var next = new Dictionary<State, State>(new LocationComparer());
            foreach (var state in beam)
                foreach (var child in Expand(state, info, item, itemEnd))
                    if (!next.TryGetValue(child, out var existing) || child.Cost < existing.Cost)
                        next[child] = child;

            if (next.Count == 0)
                throw new NotSupportedException(
                    $"MOS6502BeamPlacement: no placement for {Describe(info.Instrs[item])} in @{Function.Name}.");
            beam = [.. next.Values.OrderBy(s => s.Cost).Take(Math.Max(1, options.BeamWidth))];
        }

        State? best = null;
        foreach (var state in beam)
        {
            var finished = Finish(state, info, end);
            if (finished is not null && (best is null || finished.Cost < best.Cost))
                best = finished;
        }
        if (best is null)
            throw new NotSupportedException("MOS6502BeamPlacement: a stretch of operations cannot be completed.");

        Replay(best);
    }

    private static string Describe(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        return $"{dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}";
    }

    private void Replay(State last)
    {
        var path = new List<State>();
        for (var s = last; s is not null; s = s.Prev) path.Add(s);
        path.Reverse();
        foreach (var state in path)
            foreach (var step in state.Steps)
            {
                if (step.Action is not null) step.Action(this);
                else EmitVariant(step.Variant!, Resolve(step.Mem));
            }
    }

    // The values live after the stretch have to be in their homes.
    private State? Finish(State state, BlockInfo info, int end)
    {
        var work = new Work(this, info, state, end, end);
        if (!work.StoreLive()) return null;
        return work.Freeze(state, normalise: false);
    }

    // ------------------------------------------------------------------
    // Expanding one operation
    // ------------------------------------------------------------------

    // Each operation is placed twice, once saving a value that is about to be
    // overwritten by storing it and once by moving it to a free register.
    private IEnumerable<State> Expand(State state, BlockInfo info, int pos, int itemEnd)
    {
        var results = new List<State>();
        ExpandOne(state, info, pos, itemEnd, preferMove: false, results);
        ExpandOne(state, info, pos, itemEnd, preferMove: true, results);
        return results;
    }

    private void ExpandOne(State state, BlockInfo info, int pos, int itemEnd, bool preferMove, List<State> results)
    {
        var instr = info.Instrs[pos];

        void Add(Work? work)
        {
            if (work is not null) results.Add(work.Freeze(state, normalise: true));
        }
        Work Start() => new(this, info, state, pos, itemEnd - 1) { PreferMove = preferMove };

        if (IsCopy(instr))
        {
            Add(Start().CopyRun(info.Instrs.GetRange(pos, itemEnd - pos), itemEnd));
            return;
        }
        if (IsNoCode(instr))
        {
            Add(Start());
            return;
        }
        if (IsJump(instr))
        {
            Add(Start().Jump(instr));
            return;
        }

        switch ((MOS6502Op)instr.Opcode.Code)
        {
            case MOS6502Op.Const:
                // Immediates are read from the instruction; an address byte can be
                // rematerialised. Neither needs code here.
                Add(Start());
                break;

            case MOS6502Op.Adc:
            case MOS6502Op.Sbc:
            {
                var op = (MOS6502Op)instr.Opcode.Code == MOS6502Op.Adc ? MOS6502Operation.Adc : MOS6502Operation.Sbc;
                var commutative = op == MOS6502Operation.Adc;
                AluPlans(instr, info, op, a: 2, b: 3, carryIn: 4, commutative, Start, Add);
                break;
            }
            case MOS6502Op.And:
                AluPlans(instr, info, MOS6502Operation.And, a: 1, b: 2, carryIn: -1, commutative: true, Start, Add);
                break;
            case MOS6502Op.Ora:
                AluPlans(instr, info, MOS6502Operation.Ora, a: 1, b: 2, carryIn: -1, commutative: true, Start, Add);
                break;
            case MOS6502Op.Eor:
                AluPlans(instr, info, MOS6502Operation.Eor, a: 1, b: 2, carryIn: -1, commutative: true, Start, Add);
                break;

            case MOS6502Op.Inc:
            case MOS6502Op.Dec:
            {
                var inc = (MOS6502Op)instr.Opcode.Code == MOS6502Op.Inc;
                foreach (var reg in new[] { X, Y, A })
                {
                    var work = Start();
                    Add(work.StepRegister(instr, reg, inc ? MOS6502Operation.Inc : MOS6502Operation.Dec));
                }
                break;
            }

            case MOS6502Op.Shle:
            case MOS6502Op.Lshre:
            {
                var left = (MOS6502Op)instr.Opcode.Code == MOS6502Op.Shle;
                Add(Start().Shift(instr, left));
                break;
            }

            case MOS6502Op.Cmp:
            case MOS6502Op.CmpBr:
                foreach (var reg in new[] { A, X, Y })
                    Add(Start().Compare(instr, reg));
                break;

            case MOS6502Op.Load:
                foreach (var reg in new[] { A, X, Y })
                    Add(Start().LoadByte(instr, reg));
                break;

            case MOS6502Op.Store:
                foreach (var reg in new[] { A, X, Y })
                    Add(Start().StoreByte(instr, reg));
                break;

            default:
                throw new NotSupportedException($"MOS6502BeamPlacement: no placement for {Describe(instr)}.");
        }
        return;
    }

    private void AluPlans(
        MirInstruction instr, BlockInfo info, MOS6502Operation op, int a, int b, int carryIn, bool commutative,
        Func<Work> start, Action<Work?> add)
    {
        add(start().Alu(instr, op, a, b, carryIn));
        if (commutative)
            add(start().Alu(instr, op, b, a, carryIn));
    }

    // ------------------------------------------------------------------
    // A working copy of a state, and the moves that change it
    // ------------------------------------------------------------------

    private sealed class Work
    {
        private readonly BeamPlacer _placer;
        private readonly BlockInfo _info;
        private readonly int _pos;
        private readonly int _normPos;
        private readonly List<Step> _steps = [];
        private readonly int[] _r;
        private int _c, _nz, _ptrLo, _ptrHi;
        private readonly ulong[] _h;
        private readonly ulong[] _f;
        private long _cost;

        public bool PreferMove { get; init; }

        public Work(BeamPlacer placer, BlockInfo info, State from, int pos, int normPos)
        {
            (_placer, _info, _pos, _normPos) = (placer, info, pos, normPos);
            _f = (ulong[])from.F.Clone();
            _r = (int[])from.R.Clone();
            (_c, _nz, _ptrLo, _ptrHi) = (from.C, from.Nz, from.PtrLo, from.PtrHi);
            _h = (ulong[])from.H.Clone();
            _cost = from.Cost;
        }

        public State Freeze(State parent, bool normalise)
        {
            if (normalise)
            {
                for (var k = 0; k < 3; k++) _r[k] = Live(_r[k]);
                _c = Live(_c);
                _nz = Live(_nz);
                _ptrLo = Live(_ptrLo);
                _ptrHi = Live(_ptrHi);
                for (var i = 0; i < _info.Count; i++)
                    if (_info.LastUse[i] <= _normPos)
                    {
                        _h[i >> 6] &= ~(1UL << (i & 63));
                        _f[i >> 6] &= ~(1UL << (i & 63));
                    }
            }
            return new State
            {
                R = _r, C = _c, Nz = _nz, PtrLo = _ptrLo, PtrHi = _ptrHi, H = _h, F = _f,
                Cost = _cost, Prev = parent, Steps = [.. _steps],
            };
        }

        // A content that is still needed after the operation, else nothing.
        private int Live(int content) =>
            content >= 0 && content < ImmBase && _info.LastUse[content] <= _normPos ? Empty : content;

        // -- queries --------------------------------------------------------

        private bool InH(int i) => (_h[i >> 6] & (1UL << (i & 63))) != 0;
        private void SetH(int i) => _h[i >> 6] |= 1UL << (i & 63);
        private bool InF(int i) => i >= 0 && i < ImmBase && (_f[i >> 6] & (1UL << (i & 63))) != 0;
        private void SetF(int i) => _f[i >> 6] |= 1UL << (i & 63);
        private int VregOf(int i) => _info.Vreg[i];
        private bool IsRemat(int i) => i >= 0 && i < ImmBase && _placer._remat.ContainsKey(VregOf(i));

        // Whether the value is read by the current operation or later.
        private bool Needed(int content, bool after) =>
            content >= 0 && content < ImmBase
            && (after || _consumed.Contains(content) ? _info.LastUse[content] > _pos : _info.LastUse[content] >= _pos);

        // Values the current operation has already used up (a branch's arguments, once
        // they are in the parameters' homes).
        private readonly HashSet<int> _consumed = [];

        private int FindRegister(int content, int except = -1)
        {
            for (var k = 0; k < 3; k++)
                if (k != except && _r[k] == content) return k;
            return -1;
        }

        private int ContentOf(MirOperand operand)
        {
            if (_placer.TryConst(operand, out var value)) return ImmContent(value);
            return _info.Index[((VirtualReg)operand).Id];
        }

        private int ResultIndex(MirOperand def) => _info.Index[((VirtualReg)def).Id];

        // -- emission -------------------------------------------------------

        private long Weigh(Variant variant) =>
            (long)variant.Bytes * _placer.Options.SizeWeight
            + (long)variant.Cycles * _placer.Options.SpeedWeight * _info.Frequency;

        private void Do(Variant variant, MemRef mem)
        {
            _steps.Add(new Step { Variant = variant, Mem = mem });
            _cost += Weigh(variant);
        }

        private void Do(Action<BeamPlacer> action) => _steps.Add(new Step { Action = action });

        private static Variant V(MOS6502Operation op, Place register, Place memory, string? name = null) =>
            MOS6502Variants.Require(op, register, memory, name);

        // -- saving ---------------------------------------------------------

        // Makes the content of a register safe to overwrite: stores it unless
        // nothing needs it, a copy exists, or it can be rebuilt.
        private void SaveRegister(int register, bool after, int pinned = 0)
        {
            var content = _r[register];
            if (content < 0 || content >= ImmBase || !Needed(content, after)) return;
            if (InH(content) || InF(content) || IsRemat(content) || FindRegister(content, register) >= 0) return;

            // Move it to a register that is free instead: a transfer is cheaper than
            // a store, and so is the move back.
            if (PreferMove)
            {
                foreach (var target in new[] { X, A, Y })
                {
                    if (target == register || (pinned & (1 << target)) != 0 || !IsFree(target)) continue;
                    var transfer = MOS6502Variants.Find(
                        MOS6502Operation.Transfer, RegisterPlace(target), RegisterPlace(register));
                    if (transfer is null) continue;
                    Do(transfer, MemRef.None);
                    _r[target] = content;
                    _nz = content;
                    return;
                }
            }
            Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home), MemRef.HomeOf(VregOf(content)));
            SetH(content);
        }

        // Whether the register's content can be overwritten without being saved.
        private bool IsFree(int register)
        {
            var content = _r[register];
            return content == Empty || IsImm(content) || !Needed(content, after: false)
                || InH(content) || InF(content) || IsRemat(content) || FindRegister(content, register) >= 0;
        }

        // Stores a carry that something is about to overwrite, if anything still
        // needs it.
        private bool SaveCarry(int consumedNow, int pinned)
        {
            var c = _c;
            if (c < 0 || c >= ImmBase || !Needed(c, after: true) || InH(c)) return true;
            return StoreCarry(preserve: consumedNow == c, pinned);
        }

        // LDA #0; ROL; STA home, and when the operation still reads the carry, LSR to
        // put it back.
        private bool StoreCarry(bool preserve, int pinned)
        {
            var c = _c;
            if ((pinned & 1) != 0) return false;

            SaveRegister(A, after: false);
            Do(V(MOS6502Operation.Const, Place.A, Place.Imm, "imm"), MemRef.Imm(0));
            Do(V(MOS6502Operation.Shl, Place.A, Place.None, "rol"), MemRef.None);
            Do(V(MOS6502Operation.Spill, Place.A, Place.Home), MemRef.HomeOf(VregOf(c)));
            SetH(c);
            _r[A] = Empty;
            _nz = Empty;
            if (preserve)
                Do(V(MOS6502Operation.Lshr, Place.A, Place.None, "lsr"), MemRef.None);
            else
                _c = Empty;
            return true;
        }

        // The carry holds `want` (a known 0 / 1, or a value): a SEC / CLC, or the
        // stored bit shifted back into C.
        private bool PrepareCarry(MirOperand carryIn)
        {
            var want = ContentOf(carryIn);
            if (_c == want) return true;

            if (!SaveCarry(consumedNow: want, pinned: 0)) return false;
            if (_c == want) return true;

            if (IsImm(want))
            {
                Do(V(MOS6502Operation.Const, Place.Carry, Place.Imm, ImmValue(want) == 0 ? "clc" : "sec"), MemRef.None);
                _c = want;
                return true;
            }

            if (!InH(want)) return false;
            SaveRegister(A, after: false);
            Do(V(MOS6502Operation.Reload, Place.A, Place.Home), MemRef.HomeOf(VregOf(want)));
            Do(V(MOS6502Operation.Lshr, Place.A, Place.None, "lsr"), MemRef.None);
            _r[A] = Empty;
            _nz = Empty;
            _c = want;
            return true;
        }

        // -- moves ----------------------------------------------------------

        // Puts `want` (a value, or a known byte) in the register.
        private bool PutIn(int register, int want, int pinned = 0)
        {
            if (_r[register] == want) return true;

            Variant? bestVariant = null;
            MemRef bestMem = default;
            var bestSource = -1;
            long bestCost = long.MaxValue;
            void Consider(Variant? variant, MemRef mem, int source = -1)
            {
                if (variant is null) return;
                var cost = Weigh(variant);
                if (cost < bestCost) (bestVariant, bestMem, bestSource, bestCost) = (variant, mem, source, cost);
            }

            for (var from = 0; from < 3; from++)
                if (from != register && _r[from] == want)
                    Consider(MOS6502Variants.Find(MOS6502Operation.Transfer, RegisterPlace(register), RegisterPlace(from)),
                        MemRef.None, from);
            if (IsImm(want))
                Consider(MOS6502Variants.Find(MOS6502Operation.Const, RegisterPlace(register), Place.Imm, "imm"),
                    MemRef.Imm(ImmValue(want)));
            else if (IsRemat(want))
            {
                var (symbol, half) = _placer._remat[VregOf(want)];
                Consider(MOS6502Variants.Find(MOS6502Operation.Const, RegisterPlace(register), Place.Abs,
                    half == 0 ? "symlo" : "symhi"), MemRef.Sym(symbol));
            }
            if (want >= 0 && !IsImm(want) && InF(want))
                Consider(MOS6502Variants.Find(MOS6502Operation.Reload, RegisterPlace(register), Place.Home),
                    MemRef.Phys(_info.FixedReg[want]));
            if (want >= 0 && !IsImm(want) && InH(want))
                Consider(MOS6502Variants.Find(MOS6502Operation.Reload, RegisterPlace(register), Place.Home),
                    MemRef.HomeOf(VregOf(want)));

            if (bestVariant is null)
            {
                // An address byte into Y: through A.
                if (register == Y && IsRemat(want))
                {
                    if ((pinned & 1) != 0 || !PutIn(A, want, pinned)) return false;
                    SaveRegister(Y, after: false);
                    Do(V(MOS6502Operation.Transfer, Place.Y, Place.A), MemRef.None);
                    _r[Y] = want;
                    _nz = Empty;
                    return true;
                }

                // A value that sits in the other index register only: through its home.
                var from = FindRegister(want);
                if (from < 0 || !EnsureHome(want, pinned)) return false;
                return PutIn(register, want, pinned);
            }

            SaveRegister(register, after: false, bestSource >= 0 ? pinned | (1 << bestSource) : pinned);
            Do(bestVariant, bestMem);
            _r[register] = want;
            _nz = IsImm(want) || IsRemat(want) ? Empty : want;
            return true;
        }

        // The value is in its home (stored from a register or rebuilt through one).
        private bool EnsureHome(int content, int pinned)
        {
            if (IsImm(content) || InH(content)) return true;

            var register = FindRegister(content);
            if (register >= 0)
            {
                Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home), MemRef.HomeOf(VregOf(content)));
                SetH(content);
                return true;
            }

            if (InF(content))
            {
                var scratch = PickScratch(pinned);
                if (scratch < 0 || !PutIn(scratch, content, pinned)) return false;
                Do(V(MOS6502Operation.Spill, RegisterPlace(scratch), Place.Home), MemRef.HomeOf(VregOf(content)));
                SetH(content);
                return true;
            }

            if (IsRemat(content))
            {
                // Only A and X can load an address byte.
                var scratch = PickScratch(pinned, onlyAx: true);
                if (scratch < 0 || !PutIn(scratch, content, pinned)) return false;
                Do(V(MOS6502Operation.Spill, RegisterPlace(scratch), Place.Home), MemRef.HomeOf(VregOf(content)));
                SetH(content);
                return true;
            }

            if (_c == content) return StoreCarry(preserve: true, pinned);
            return false;
        }

        // The register cheapest to overwrite that is not pinned.
        private int PickScratch(int pinned, bool onlyAx = false)
        {
            var best = -1;
            var bestCost = long.MaxValue;
            for (var k = 0; k < 3; k++)
            {
                if ((pinned & (1 << k)) != 0 || (onlyAx && k == Y)) continue;
                var cost = 0L;
                var content = _r[k];
                if (content >= 0 && content < ImmBase && Needed(content, after: false)
                    && !InH(content) && !IsRemat(content) && FindRegister(content, k) < 0)
                    cost = 100;
                else if (content == Empty)
                    cost = -1;
                if (cost < bestCost) (best, bestCost) = (k, cost);
            }
            return best;
        }

        // The operand of an ALU or compare variant that is not in a register: an
        // immediate or a home.
        private bool MemorySide(int content, int pinned, out Place place, out MemRef mem)
        {
            if (IsImm(content))
            {
                (place, mem) = (Place.Imm, MemRef.Imm(ImmValue(content)));
                return true;
            }
            if (!InH(content) && InF(content))
            {
                (place, mem) = (Place.Home, MemRef.Phys(_info.FixedReg[content]));
                return true;
            }
            (place, mem) = (Place.Home, MemRef.HomeOf(VregOf(content)));
            return EnsureHome(content, pinned);
        }

        // The carry flag after an operation that sets it as a result.
        private void SetCarryResult(MirOperand carryOut)
        {
            var content = ResultIndex(carryOut);
            _c = Needed(content, after: true) ? content : Empty;
        }

        // -- the operations -------------------------------------------------

        // %r [, %cout] = adc|sbc|and|ora|eor with the operand at `reg` in A and the
        // one at `mem` in memory.
        public Work? Alu(MirInstruction instr, MOS6502Operation op, int reg, int mem, int carryIn)
        {
            var hasCarry = carryIn >= 0;
            if (hasCarry && !PrepareCarry(instr.Operands[carryIn])) return null;

            var registerOperand = ContentOf(instr.Operands[reg]);
            var memoryOperand = ContentOf(instr.Operands[mem]);
            if (!MemorySide(memoryOperand, pinned: _r[A] == registerOperand ? 1 : 0, out var place, out var memRef))
                return null;
            if (!PutIn(A, registerOperand, pinned: 0)) return null;

            var result = ResultIndex(instr.Operands[0]);
            SaveRegister(A, after: true);
            Do(V(op, Place.A, place), memRef);
            _r[A] = Needed(result, after: true) ? result : Empty;
            _nz = result;
            if (hasCarry) SetCarryResult(instr.Operands[1]);
            return this;
        }

        // %r = inc|dec %a with the byte in X, Y or A.
        public Work? StepRegister(MirInstruction instr, int register, MOS6502Operation op)
        {
            var source = ContentOf(instr.Operands[1]);
            if (register == A && !SaveCarry(consumedNow: Empty, pinned: 0)) return null;
            if (!PutIn(register, source)) return null;

            var result = ResultIndex(instr.Operands[0]);
            SaveRegister(register, after: true);
            var variant = V(op, RegisterPlace(register), register == A ? Place.Imm : Place.None);
            Do(variant, register == A ? MemRef.Imm(1) : MemRef.None);
            _r[register] = Needed(result, after: true) ? result : Empty;
            _nz = result;
            if (register == A) _c = Empty;
            return this;
        }

        // %r, %cout = shle|lshre %a, %cin, in A.
        public Work? Shift(MirInstruction instr, bool left)
        {
            var carryIn = instr.Operands[3];
            var plain = _placer.TryConst(carryIn, out var constant) && constant == 0;
            if (plain)
            {
                if (!SaveCarry(consumedNow: Empty, pinned: 0)) return null;
            }
            else if (!PrepareCarry(carryIn))
                return null;

            if (!PutIn(A, ContentOf(instr.Operands[2]))) return null;

            var result = ResultIndex(instr.Operands[0]);
            SaveRegister(A, after: true);
            var name = left ? (plain ? "asl" : "rol") : (plain ? "lsr" : "ror");
            Do(V(left ? MOS6502Operation.Shl : MOS6502Operation.Lshr, Place.A, Place.None, name), MemRef.None);
            _r[A] = Needed(result, after: true) ? result : Empty;
            _nz = result;
            SetCarryResult(instr.Operands[1]);
            return this;
        }

        // mos6502.cmp (the carry of a compare) and mos6502.cmp_br, comparing the
        // register `reg` with an immediate or a home.
        public Work? Compare(MirInstruction instr, int reg)
        {
            var isBranch = (MOS6502Op)instr.Opcode.Code == MOS6502Op.CmpBr;
            var left = ContentOf(instr.Operands[isBranch ? 0 : 1]);
            var right = ContentOf(instr.Operands[isBranch ? 1 : 2]);

            var zeroTest = isBranch && right == ImmContent(0) && ZeroTestExits(instr);
            // A compare clobbers the carry; a stored one is not lost.
            if (!(zeroTest && _nz == left && FindRegister(left) >= 0) && !SaveCarry(consumedNow: Empty, pinned: 0))
                return null;

            if (!MemorySide(right, pinned: 1 << reg, out var place, out var memRef)) return null;
            if (!PutIn(reg, left, pinned: 0)) return null;

            if (isBranch && !StoreLive(pinned: 1 << reg, branch: instr)) return null;

            // The compare is emitted either way; when N/Z already reflect the value the
            // late optimisation drops it, so the search does not pay for it.
            var redundant = zeroTest && _nz == left && _r[reg] == left;
            var before = _cost;
            Do(V(MOS6502Operation.Cmp, RegisterPlace(reg), place), memRef);
            if (redundant) _cost = before;
            _nz = Empty;
            _c = Empty;

            if (isBranch)
                Do(placer => placer.EmitCompareBranches(instr));
            else
                SetCarryResult(instr.Operands[0]);
            return this;
        }

        // A compare with zero whose exits only look at N and Z can use the flags a
        // load or transfer already left.
        private static bool ZeroTestExits(MirInstruction instr)
        {
            for (var k = 2; k < instr.Operands.Length; k += 2)
                if ((CmpBrCondition)((Immediate)instr.Operands[k]).Value is CmpBrCondition.Cs or CmpBrCondition.Cc)
                    return false;
            return true;
        }

        // %v = load @sym+off | %lo, %hi, off into the register.
        public Work? LoadByte(MirInstruction instr, int reg)
        {
            var result = ResultIndex(instr.Operands[0]);
            if (instr.Operands[1] is Symbol symbol)
            {
                SaveRegister(reg, after: true);
                Do(V(MOS6502Operation.Load, RegisterPlace(reg), Place.Abs), MemRef.Sym(symbol));
            }
            else
            {
                if (reg != A) return null;
                if (!SetUpPointer(instr.Operands[1], instr.Operands[2], ((Immediate)instr.Operands[3]).Value, pinned: 0))
                    return null;
                SaveRegister(A, after: true, pinned: 1 << Y);
                Do(V(MOS6502Operation.Load, Place.A, Place.Pair), MemRef.None);
            }
            _r[reg] = Needed(result, after: true) ? result : Empty;
            _nz = result;
            return this;
        }

        // store %v, @sym+off | store %v, %lo, %hi, off from the register.
        public Work? StoreByte(MirInstruction instr, int reg)
        {
            var value = ContentOf(instr.Operands[0]);
            if (instr.Operands[1] is Symbol symbol)
            {
                if (!PutIn(reg, value)) return null;
                Do(V(MOS6502Operation.Store, RegisterPlace(reg), Place.Abs), MemRef.Sym(symbol));
                return this;
            }

            if (reg != A) return null;
            if (!SetUpPointer(instr.Operands[1], instr.Operands[2], ((Immediate)instr.Operands[3]).Value,
                    pinned: _r[A] == value ? 1 : 0))
                return null;
            if (!PutIn(A, value, pinned: 1 << Y)) return null;
            Do(V(MOS6502Operation.Store, Place.A, Place.Pair), MemRef.None);
            return this;
        }

        // The pointer bytes into the scratch pair and the offset into Y.
        private bool SetUpPointer(MirOperand lo, MirOperand hi, long offset, int pinned)
        {
            return SetPointerByte(lo, MOS6502Variants.PointerLo, ref _ptrLo, pinned)
                && SetPointerByte(hi, MOS6502Variants.PointerHi, ref _ptrHi, pinned)
                && PutIn(Y, ImmContent(offset), pinned);
        }

        private bool SetPointerByte(MirOperand operand, int pointerReg, ref int held, int pinned)
        {
            var want = ContentOf(operand);
            if (held == want) return true;

            var register = FindRegister(want);
            if (register < 0)
            {
                register = PickScratch(pinned | (1 << Y), onlyAx: IsRemat(want));
                if (register < 0)
                    register = PickScratch(pinned, onlyAx: IsRemat(want));
                if (register < 0 || !PutIn(register, want, pinned)) return false;
            }
            Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home), MemRef.Phys(pointerReg));
            held = want;
            return true;
        }

        // Stores every value the rest of the block, or a successor, still needs.
        // For a conditional branch that includes what its targets take as
        // arguments; `needFrom` names the position from which a use counts (the
        // barrier after a run of copies); `pinned` registers are left alone.
        public bool StoreLive(
            int pinned = 0, MirInstruction? branch = null, int needFrom = int.MinValue, int defLimit = int.MinValue)
        {
            var limit = defLimit == int.MinValue ? _pos : defLimit;
            var arguments = new HashSet<int>();
            if (branch is not null)
                foreach (var operand in branch.Operands)
                    if (operand is BlockTarget target)
                        foreach (var arg in target.Args)
                            if (arg is VirtualReg v && _info.Index.TryGetValue(v.Id, out var index))
                                arguments.Add(index);

            var live = new List<int>();
            for (var i = 0; i < _info.Count; i++)
            {
                if (_info.DefPos[i] >= limit || InH(i)) continue;
                var needed =
                    branch is not null ? _info.LastUse[i] > _pos || arguments.Contains(i)
                    : needFrom != int.MinValue ? _info.LastUse[i] >= needFrom
                    : _info.LastUse[i] >= _pos;
                if (needed) live.Add(i);
            }

            foreach (var i in live.Where(i => FindRegister(i) >= 0))
                if (!EnsureHome(i, pinned)) return false;
            foreach (var i in live.Where(i => !InH(i) && InF(i)))
                if (!EnsureHome(i, pinned)) return false;
            foreach (var i in live.Where(i => !InH(i) && IsRemat(i)))
                if (!EnsureHome(i, pinned)) return false;
            foreach (var i in live.Where(i => !InH(i) && _c == i))
                if (!EnsureHome(i, pinned)) return false;
            return live.All(InH);
        }

        // cf.br: the arguments go straight from where they are into the parameters'
        // homes. A branch whose moves read a parameter another move writes keeps the
        // naive parallel move, from the arguments' homes.
        public Work? Jump(MirInstruction instr)
        {
            var target = (BlockTarget)instr.Operands[0];
            var moves = _placer.EdgeMoves(target);
            var hazard = _placer.HasMoveHazard(moves);
            if (hazard)
            {
                if (!StoreLive(branch: instr)) return null;
                Do(placer => placer.EmitEdgeMoves(target));
            }
            else
            {
                if (!StoreLive(needFrom: _pos + 1)) return null;

                // An argument that shares its parameter's home needs no move, only to
                // be in that home.
                for (var k = 0; k < target.Args.Length; k++)
                {
                    if (target.Args[k] is not VirtualReg { IsDefinition: false } shared || _placer._constants.ContainsKey(shared.Id)) continue;
                    if (moves.Any(m => m.Parameter == target.Block.Parameters[k])) continue;
                    if (_placer._uses.GetValueOrDefault(target.Block.Parameters[k]) == 0) continue;
                    if (shared.Id == target.Block.Parameters[k]) continue;
                    var content = ContentOf(shared);
                    if (!InH(content) && !IsRemat(content) && !EnsureHome(content, 0)) return null;
                }

                foreach (var (parameter, source) in moves.OrderBy(m => FindRegister(ContentOf(m.Source)) >= 0 ? 0 : 1).ToList())
                {
                    var content = ContentOf(source);
                    if (!IsImm(content) && !InH(content) && !InF(content) && FindRegister(content) < 0
                        && !IsRemat(content) && !EnsureHome(content, 0))
                        return null;
                    var register = FindRegister(content);
                    if (register < 0)
                    {
                        register = PickScratch(0);
                        if (!PutIn(register, content)) return null;
                    }
                    Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home), MemRef.HomeOf(parameter));
                    _consumed.Add(content);
                }
            }
            Do(placer => placer.Emit(MOS6502Op.JmpAbs, new BlockTarget(target.Block, [])));
            return this;
        }

        // A run of consecutive copies, as a parallel move between values and the
        // fixed registers of a calling convention:
        //   1. bytes arriving in A, X, Y (an argument, a call's result) are values there;
        //   2. bytes arriving in zero-page registers are values living there;
        //   3. copies between values;
        //   4. before a copy into a fixed register: what the barrier after the run
        //      would lose is stored, and the zero-page arguments still needed move home;
        //   5. values into zero-page registers, then Y, X, A.
        // `nextPos` is the position of the instruction after the run.
        public Work? CopyRun(List<MirInstruction> run, int nextPos)
        {
            var fromGpr = new List<MirInstruction>();
            var fromZp = new List<MirInstruction>();
            var toVreg = new List<MirInstruction>();
            var toZp = new List<MirInstruction>();
            var toGpr = new List<MirInstruction>[3] { [], [], [] };

            foreach (var copy in run)
            {
                var (dst, src) = (copy.Operands[0], copy.Operands[1]);
                if (dst is PhysicalReg d && src is PhysicalReg s)
                {
                    if (d.Id == s.Id) continue;
                    throw new NotSupportedException(
                        $"MOS6502BeamPlacement: a copy from ${MOS6502Registers.NameOf(s.Id)} to ${MOS6502Registers.NameOf(d.Id)}.");
                }
                if (dst is PhysicalReg to)
                {
                    if (to.Id is A or X or Y) toGpr[to.Id].Add(copy);
                    else if (to.Id >= MOS6502Registers.RC(0)) toZp.Add(copy);
                    else throw new NotSupportedException(
                        $"MOS6502BeamPlacement: a copy into ${MOS6502Registers.NameOf(to.Id)}.");
                }
                else if (src is PhysicalReg from)
                    (from.Id is A or X or Y ? fromGpr : fromZp).Add(copy);
                else
                    toVreg.Add(copy);
            }

            foreach (var copy in fromGpr)
            {
                if (_placer.Unused(copy.Operands[0])) continue;
                var register = ((PhysicalReg)copy.Operands[1]).Id;
                _r[register] = ResultIndex(copy.Operands[0]);
                _nz = Empty;
            }

            foreach (var copy in fromZp)
            {
                if (_placer.Unused(copy.Operands[0])) continue;
                var value = ResultIndex(copy.Operands[0]);
                if (_placer._argRegister.ContainsKey(VregOf(value)))
                {
                    SetH(value);
                    continue;
                }
                if (_info.FixedReg[value] >= 0)
                {
                    SetF(value);
                    continue;
                }
                var scratch = PickScratch(0);
                SaveRegister(scratch, after: false);
                Do(V(MOS6502Operation.Reload, RegisterPlace(scratch), Place.Home),
                    MemRef.Phys(((PhysicalReg)copy.Operands[1]).Id));
                _r[scratch] = value;
                _nz = Empty;
            }

            foreach (var copy in toVreg)
            {
                if (_placer.Unused(copy.Operands[0])) continue;
                var source = ContentOf(copy.Operands[1]);
                var register = FindRegister(source);
                if (register < 0)
                {
                    register = PickScratch(0);
                    if (!PutIn(register, source)) return null;
                }
                SaveRegister(register, after: true);
                _r[register] = ResultIndex(copy.Operands[0]);
                _nz = Empty;
            }

            var setsUp = toZp.Count > 0 || toGpr.Any(l => l.Count > 0);
            if (setsUp)
            {
                if (!StoreLive(needFrom: nextPos, defLimit: nextPos)) return null;
                if (toZp.Count > 0)
                {
                    for (var i = 0; i < _info.Count; i++)
                        if (InF(i) && !InH(i) && _info.LastUse[i] >= _pos && !EnsureHome(i, 0)) return null;
                    foreach (var copy in toZp)
                    {
                        var written = ((PhysicalReg)copy.Operands[0]).Id;
                        for (var i = 0; i < _info.Count; i++)
                            if (_info.FixedReg[i] == written) _f[i >> 6] &= ~(1UL << (i & 63));
                    }
                }
            }

            foreach (var copy in toZp)
            {
                var source = ContentOf(copy.Operands[1]);
                var register = FindRegister(source);
                if (register < 0)
                {
                    register = PickScratch(0);
                    if (!PutIn(register, source)) return null;
                }
                Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home),
                    MemRef.Phys(((PhysicalReg)copy.Operands[0]).Id));
            }

            var pinned = 0;
            foreach (var register in new[] { Y, X, A })
            {
                foreach (var copy in toGpr[register])
                    if (!PutIn(register, ContentOf(copy.Operands[1]), pinned)) return null;
                if (toGpr[register].Count > 0) pinned |= 1 << register;
            }
            return this;
        }
    }

    // The branches of a cmp_br after its compare: one per exit, through a block of
    // moves when the exit passes arguments.
    internal void EmitCompareBranches(MirInstruction instr) => EmitBranches(instr);
}
