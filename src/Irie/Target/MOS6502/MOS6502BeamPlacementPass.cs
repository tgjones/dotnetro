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
    // How many blocks ahead the exit state of a block is judged by the cost of the
    // blocks that inherit it; 0 judges a block by itself.
    public int Lookahead { get; init; } = 3;
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
    private const int ImmBase = Snapshot.ImmBase;
    private static int ImmContent(long value) => ImmBase + (int)(value & 0xFF);
    private static bool IsImm(int content) => content >= ImmBase;
    private static long ImmValue(int content) => content - ImmBase;

    private LoopInfo _loops = null!;

    internal BeamPlacementOptions Options => options;

    // ------------------------------------------------------------------
    // Liveness
    // ------------------------------------------------------------------

    // Arguments that arrive in zero-page registers and stay there, as their own homes.
    private readonly Dictionary<int, int> _argRegister = [];

    // Values whose only use is a copy into a fixed zero-page register (a return value,
    // a call argument) and that nothing disturbs that register for: they are built
    // in that register, as their own home.
    private readonly Dictionary<int, int> _destinationRegister = [];

    private bool TryFixedHome(int vreg, out int register) =>
        _argRegister.TryGetValue(vreg, out register) || _destinationRegister.TryGetValue(vreg, out register);

    internal override PhysicalReg Home(int vreg) =>
        TryFixedHome(vreg, out var register)
            ? new PhysicalReg(register, IsDefinition: false)
            : base.Home(vreg);

    internal override bool TryGetHome(int vreg, out int register)
    {
        if (TryFixedHome(vreg, out register)) return true;
        return base.TryGetHome(vreg, out register);
    }

    // Values that are never live together and are linked by a block parameter or a
    // copy share a home: the representative of each such class.
    private Dictionary<int, int> _homeClass = [];

    internal override int HomeKey(int vreg) => _homeClass.GetValueOrDefault(vreg, vreg);

    // The arguments that arrive in zero-page registers and can stay there: those
    // that are dead by the time anything overwrites their register. A call (the
    // callee may use any of the registers) and a copy into a fixed register
    // overwrite; so does any other instruction that defines a zero-page register.
    private void FindArgumentsInPlace()
    {
        var candidates = new Dictionary<int, int>();
        foreach (var block in Function.Blocks)
            foreach (var instr in block.Instructions)
                if (IsCopyFromZeroPage(instr) && instr.Operands[0] is VirtualReg d && !Unused(d))
                    candidates[d.Id] = ((PhysicalReg)instr.Operands[1]).Id;

        foreach (var block in Function.Blocks)
        {
            var live = new HashSet<int>(_liveOut[block]);
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                foreach (var operand in instr.Operands)
                    if (operand is PhysicalReg { IsDefinition: true, Id: var written } && written >= MOS6502Registers.RC(2)
                        && !MOS6502Registers.IsHome(written))
                    {
                        foreach (var vreg in live)
                            if (candidates.TryGetValue(vreg, out var register) && register == written)
                                candidates.Remove(vreg);

                        // A run of copies is one parallel move that writes the zero-page
                        // registers before it reads anything into A, X, Y, so a value
                        // another copy of the run reads is overwritten too.
                        if (IsCopy(instr))
                        {
                            for (var j = i - 1; j >= 0 && IsCopy(block.Instructions[j]); j--)
                                RemoveReadersOf(block.Instructions[j], written);
                            for (var j = i + 1; j < block.Instructions.Count && IsCopy(block.Instructions[j]); j++)
                                RemoveReadersOf(block.Instructions[j], written);
                        }
                    }
                foreach (var def in Defs(instr)) live.Remove(def.Id);
                foreach (var use in Uses(instr)) live.Add(use.Id);
            }
        }
        foreach (var (vreg, register) in candidates) _argRegister[vreg] = register;
        FindDestinationHomes();

        void RemoveReadersOf(MirInstruction other, int written)
        {
            if (other.Operands[1] is VirtualReg read && candidates.TryGetValue(read.Id, out var register) && register == written)
                candidates.Remove(read.Id);
        }
    }

    private bool DefinesRegister(MirInstruction instr, int register) =>
        instr.Operands.Any(o => o is PhysicalReg { IsDefinition: true } p && p.Id == register);

    private void FindDestinationHomes()
    {
        foreach (var block in Function.Blocks)
        {
            var instrs = block.Instructions;
            var defAt = new Dictionary<int, int>();

            // The register a value arrived in, while it has not been moved.
            var arrived = new Dictionary<int, int>();
            foreach (var instr in instrs)
                if (IsCopyFromZeroPage(instr) && instr.Operands[0] is VirtualReg d)
                    arrived[d.Id] = ((PhysicalReg)instr.Operands[1]).Id;
            var wanted = new Dictionary<int, int>();   // vreg -> register
            for (var i = 0; i < instrs.Count; i++)
            {
                foreach (var def in Defs(instrs[i])) defAt[def.Id] = i;
                if (!IsCopy(instrs[i]) || instrs[i].Operands[0] is not PhysicalReg { Id: var register }
                    || instrs[i].Operands[1] is not VirtualReg { IsDefinition: false } value
                    || register < MOS6502Registers.RC(2) || MOS6502Registers.IsHome(register)
                    || register == MOS6502Variants.PointerLo || register == MOS6502Variants.PointerHi
                    || _uses.GetValueOrDefault(value.Id) != 1 || !NeedsHome(value.Id)
                    || _argRegister.ContainsKey(value.Id) || arrived.ContainsKey(value.Id) || _remat.ContainsKey(value.Id)
                    || !defAt.TryGetValue(value.Id, out var def2))
                    continue;
                var undisturbed = true;
                for (var k = def2 + 1; k < i && undisturbed; k++)
                    undisturbed = !DefinesRegister(instrs[k], register);

                // A run of copies is one parallel move: no other copy of it may write
                // the register either.
                for (var k = i - 1; k >= 0 && undisturbed && IsCopy(instrs[k]); k--)
                    undisturbed = !DefinesRegister(instrs[k], register);
                for (var k = i + 1; k < instrs.Count && undisturbed && IsCopy(instrs[k]); k++)
                    undisturbed = !DefinesRegister(instrs[k], register);
                if (undisturbed) wanted[value.Id] = register;
            }
            if (wanted.Count == 0) continue;

            // The register must not hold something else that is still needed when
            // the value is built.
            var live = new HashSet<int>(_liveOut[block]);
            for (var i = instrs.Count - 1; i >= 0; i--)
            {
                foreach (var def in Defs(instrs[i]))
                {
                    if (wanted.TryGetValue(def.Id, out var register)
                        && !live.Any(v => v != def.Id
                            && (TryFixedHome(v, out var other) && other == register
                                || arrived.TryGetValue(v, out var from) && from == register)))
                        _destinationRegister[def.Id] = register;
                    live.Remove(def.Id);
                }
                foreach (var use in Uses(instrs[i])) live.Add(use.Id);
            }
        }
    }

    internal override void BeforePlacement()
    {
        FindArgumentsInPlace();

        _loops = new LoopAnalysis().Compute(Function);
        FindInheriting();
        _homeClass = HomeClasses.Build(this, _loops, vreg => TryFixedHome(vreg, out _) || _remat.ContainsKey(vreg));
    }

    // ------------------------------------------------------------------
    // Block order and the blocks that inherit registers
    // ------------------------------------------------------------------

    // A block whose incoming edges all come from branches the search places starts
    // from a snapshot of the registers instead of from empty registers with every
    // live value in its home. With one incoming edge that is the registers the
    // predecessor left; a join (several incoming edges) takes the registers of the
    // first edge placed into it, its primary, and every other edge reconciles to
    // that state. A loop header is a join whose primary is the edge from outside,
    // so the state is chosen before the body is placed and the back edge pays to
    // match it. A block placed earlier has to know which of its successors start
    // from a snapshot, since it then leaves their values where they are instead of
    // storing them.
    private List<MirBlock> _order = [];
    private readonly HashSet<MirBlock> _inheriting = [];
    private readonly Dictionary<MirBlock, MirBlock> _primary = [];
    private readonly Dictionary<MirBlock, Snapshot> _entry = [];

    internal override IReadOnlyList<MirBlock> PlacementOrder() => _order;

    internal override bool IsInheriting(MirBlock block) => _inheriting.Contains(block);

    internal override ExitRole RoleOf(MirBlock from, MirBlock to)
    {
        if (!_inheriting.Contains(to)) return ExitRole.Conventional;
        if (!_primary.TryGetValue(to, out var primary)) return ExitRole.Single;
        return from == primary ? ExitRole.Primary : ExitRole.Secondary;
    }

    internal override Snapshot? Contract(MirBlock block) => _entry.GetValueOrDefault(block);

    private void FindInheriting()
    {
        // Reverse post-order from the entry puts a block after the blocks that reach
        // it by a forward edge; unreachable blocks follow in layout order.
        var visited = new HashSet<MirBlock>();
        var post = new List<MirBlock>();
        var stack = new Stack<(MirBlock Block, IEnumerator<MirBlock> Next)>();
        if (Function.Blocks.Count > 0)
        {
            visited.Add(Function.Blocks[0]);
            stack.Push((Function.Blocks[0], Function.Blocks[0].Successors.ToList().GetEnumerator()));
        }
        while (stack.Count > 0)
        {
            var (block, next) = stack.Peek();
            if (next.MoveNext())
            {
                if (visited.Add(next.Current))
                    stack.Push((next.Current, next.Current.Successors.ToList().GetEnumerator()));
            }
            else
            {
                post.Add(block);
                stack.Pop();
            }
        }
        post.Reverse();
        _order = [.. post, .. Function.Blocks.Where(b => !visited.Contains(b))];

        var position = new Dictionary<MirBlock, int>();
        for (var i = 0; i < _order.Count; i++) position[_order[i]] = i;

        // The edges into each block, as (source block, the branch that makes it).
        var incoming = new Dictionary<MirBlock, List<(MirBlock From, MirInstruction Branch)>>();
        foreach (var block in Function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is BlockTarget target)
                    {
                        if (!incoming.TryGetValue(target.Block, out var list)) incoming[target.Block] = list = [];
                        list.Add((block, instr));
                    }

        foreach (var block in Function.Blocks.Skip(1))
        {
            if (!incoming.TryGetValue(block, out var edges)) continue;

            // Every edge has to come from a branch the search places (not from
            // hand-written code), from a block that is reached, one edge per block.
            if (edges.Any(e => !IsJump(e.Branch) && !IsOp(e.Branch, MOS6502Op.CmpBr))
                || edges.Any(e => !visited.Contains(e.From) || e.From == block && edges.Count == 1)
                || edges.Select(e => e.From).Distinct().Count() != edges.Count)
                continue;

            if (edges.Count == 1)
            {
                if (block.Parameters.Count == 0 && position[edges[0].From] < position[block])
                    _inheriting.Add(block);
                continue;
            }

            var primary = edges.Select(e => e.From).MinBy(f => position[f])!;
            if (position[primary] >= position[block]) continue;
            _inheriting.Add(block);
            _primary[block] = primary;
        }
    }

    // The state a block starts in when it inherits its predecessor's registers.
    private State InheritedState(BlockInfo info, Snapshot snapshot)
    {
        var words = (info.Count + 63) / 64;
        var state = new State { H = new ulong[words], F = new ulong[words] };

        int Local(int content)
        {
            if (content < 0 || content >= ImmBase) return content;
            return info.Index.TryGetValue(content, out var i) && info.DefPos[i] < 0 && info.End(i) >= 0 ? i : Empty;
        }

        for (var k = 0; k < 3; k++) state.R[k] = Local(snapshot.R[k]);
        state.C = Local(snapshot.C);
        state.PtrLo = Local(snapshot.PtrLo);
        state.PtrHi = Local(snapshot.PtrHi);
        for (var i = 0; i < info.Count; i++)
            if (info.DefPos[i] < 0 && snapshot.Homed.Contains(info.Vreg[i]))
                state.H[i >> 6] |= 1UL << (i & 63);

        for (var i = 0; i < info.Count; i++)
        {
            if (info.DefPos[i] >= 0 || info.End(i) < 0) continue;
            var held = (state.H[i >> 6] & (1UL << (i & 63))) != 0
                || state.R.Contains(i) || state.C == i || _remat.ContainsKey(info.Vreg[i]);
            if (!held)
                throw new InvalidOperationException(
                    $"MOS6502BeamPlacement: v{info.Vreg[i]} is live into a block of @{Function.Name} but its predecessor left it nowhere.");
        }
        return state;
    }

    // ------------------------------------------------------------------
    // One block
    // ------------------------------------------------------------------

    // Per-block tables: dense indices for the values the block touches, where each
    // is defined and last used.
    private sealed class BlockInfo
    {
        public required MirBlock Block;
        public required List<MirInstruction> Instrs;
        public required Dictionary<int, int> Index;
        public required List<int> Vreg;
        public required int[] DefPos;
        // The last operation that reads the value, -1 if none.
        public required int[] LastUse;
        // The last branch whose targets take the value in (live into a successor).
        public required int[] ExitUse;
        public required long Frequency;
        // The zero-page register a value arrived in (an argument), or -1.
        public required int[] FixedReg;
        // The global a value is a load of that can be left in memory and read from
        // there by its uses (nothing writes memory in between), or null.
        public required Symbol?[] FoldedLoad;
        public int Count => Vreg.Count;
        // The position from which the value is not needed any more.
        public int End(int i) => Math.Max(LastUse[i], ExitUse[i]);
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
        // A block can start with a live-in value in a register, so every one is
        // tracked, whether the block reads it or only passes it on.
        foreach (var vreg in _liveIn[block].Order())
            Touch(vreg, -1, isDef: false);
        for (var pos = 0; pos < instrs.Count; pos++)
        {
            foreach (var use in Uses(instrs[pos])) Touch(use.Id, pos, isDef: false);
            foreach (var def in Defs(instrs[pos])) Touch(def.Id, pos, isDef: true);
        }

        // A value that flows into a successor is needed up to the branch that goes
        // there, not to the end of the block.
        var exitUse = Enumerable.Repeat(-1, vregs.Count).ToArray();
        for (var pos = 0; pos < instrs.Count; pos++)
            foreach (var operand in instrs[pos].Operands)
                if (operand is BlockTarget target)
                    foreach (var vreg in _liveIn[target.Block])
                        if (index.TryGetValue(vreg, out var i))
                            exitUse[i] = Math.Max(exitUse[i], pos);

        var fixedReg = Enumerable.Repeat(-1, vregs.Count).ToArray();
        foreach (var instr in instrs)
            if (IsCopyFromZeroPage(instr) && instr.Operands[0] is VirtualReg d && index.TryGetValue(d.Id, out var di))
                fixedReg[di] = ((PhysicalReg)instr.Operands[1]).Id;

        var foldedLoad = new Symbol?[vregs.Count];
        for (var pos = 0; pos < instrs.Count; pos++)
        {
            if (!IsOp(instrs[pos], MOS6502Op.Load) || instrs[pos].Operands[1] is not Symbol global
                || IsFrameSlot(global.Name) || instrs[pos].Operands[0] is not VirtualReg loaded
                || !index.TryGetValue(loaded.Id, out var li) || _liveOut[block].Contains(loaded.Id))
                continue;
            var readable = true;
            for (var p = pos + 1; p <= lastUse[li] && readable; p++)
                readable = IsSearchable(instrs[p]) && !IsOp(instrs[p], MOS6502Op.Store);
            if (readable && lastUse[li] > pos) foldedLoad[li] = global;
        }

        return new BlockInfo
        {
            FoldedLoad = foldedLoad,
            Block = block,
            Instrs = instrs,
            Index = index,
            Vreg = vregs,
            DefPos = [.. defPos],
            LastUse = [.. lastUse],
            ExitUse = exitUse,
            Frequency = _loops.Frequency(block),
            FixedReg = fixedReg,
        };
    }

    internal override void PlaceBlock(MirBlock block)
    {
        _out = [];
        _carryHolds = -1;

        var info = InfoOf(block);
        var instrs = info.Instrs;

        State? entry = null;
        if (IsInheriting(block))
        {
            if (!_entry.TryGetValue(block, out var snapshot))
                throw new InvalidOperationException(
                    $"MOS6502BeamPlacement: the predecessor of a block of @{Function.Name} did not hand over its registers.");
            entry = InheritedState(info, snapshot);
        }

        for (var i = 0; i < instrs.Count;)
        {
            if (IsSearchable(instrs[i]))
            {
                var start = i;
                while (i < instrs.Count && IsSearchable(instrs[i])) i++;
                RunSegment(info, start, i, entry);
            }
            else
            {
                // A barrier expects every value in its home: settle the inherited registers.
                if (entry is not null) RunSegment(info, 0, 0, entry);
                i = PlaceInstruction(instrs, i);
            }
            entry = null;
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
        public (MirBlock Target, Snapshot Snapshot)[] Snaps = [];
        // The code of the edges out of a cmp_br that need some (into a join).
        public (MirInstruction Branch, int Operand, Step[] Steps)[] Edges = [];
        // Identifies the registers handed to successors so far on this path: two
        // states that agree on the locations but handed over different things are
        // different choices, judged by what the successors then cost.
        public int SnapSig;

        public bool SameLocations(State other) =>
            R[0] == other.R[0] && R[1] == other.R[1] && R[2] == other.R[2]
            && C == other.C && Nz == other.Nz && PtrLo == other.PtrLo && PtrHi == other.PtrHi && SnapSig == other.SnapSig
            && H.AsSpan().SequenceEqual(other.H) && F.AsSpan().SequenceEqual(other.F);

        public int LocationHash()
        {
            var hash = new HashCode();
            hash.Add(R[0]); hash.Add(R[1]); hash.Add(R[2]);
            hash.Add(C); hash.Add(Nz); hash.Add(PtrLo); hash.Add(PtrHi); hash.Add(SnapSig);
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
            if (info.DefPos[i] < start && info.End(i) >= start)
                state.H[i >> 6] |= 1UL << (i & 63);
        return state;
    }

    private void RunSegment(BlockInfo info, int start, int end, State? entry = null) =>
        Replay(FindBest(info, start, end, entry, options.Lookahead));

    // The cheapest placement of the stretch [start, end). When the stretch ends in
    // branches to blocks that inherit its registers, each candidate is also charged
    // for placing the first stretch of those blocks from the registers it leaves
    // them (`lookahead` levels deep), so the exit state is chosen for what comes
    // next and not only for the stretch itself.
    private State FindBest(BlockInfo info, int start, int end, State? entry, int lookahead)
    {
        var beam = new List<State> { entry ?? InitialState(info, start) };
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
        var bestTotal = 0L;
        foreach (var state in beam)
        {
            var finished = Finish(state, info, end);
            if (finished is null) continue;
            var total = finished.Cost + (lookahead > 0 ? SuccessorCost(finished, lookahead) : 0);
            if (best is null || total < bestTotal) (best, bestTotal) = (finished, total);
        }
        if (best is null)
            throw new NotSupportedException("MOS6502BeamPlacement: a stretch of operations cannot be completed.");
        return best;
    }

    // What the first stretch of each inheriting successor costs from the registers
    // the placement leaves it.
    internal int _lookingAhead;
    // The states being judged for blocks that do not have one yet.
    internal readonly Dictionary<MirBlock, Snapshot> _provisional = [];

    private long SuccessorCost(State last, int lookahead)
    {
        _lookingAhead++;
        try { return SuccessorCostCore(last, lookahead); }
        finally { _lookingAhead--; }
    }

    private long SuccessorCostCore(State last, int lookahead)
    {
        var total = 0L;
        for (var s = last; s is not null; s = s.Prev)
            foreach (var (target, snapshot) in s.Snaps)
            {
                var info = InfoOf(target);
                var entry = InheritedState(info, snapshot);
                var end = 0;
                while (end < info.Instrs.Count && IsSearchable(info.Instrs[end])) end++;
                var had = _provisional.TryGetValue(target, out var outer);
                _provisional[target] = snapshot;
                try { total += FindBest(info, 0, end, entry, lookahead - 1).Cost; }
                finally { if (had) _provisional[target] = outer!; else _provisional.Remove(target); }
            }
        return total;
    }

    private readonly Dictionary<MirBlock, BlockInfo> _infos = [];

    private BlockInfo InfoOf(MirBlock block)
    {
        if (!_infos.TryGetValue(block, out var info)) _infos[block] = info = Analyse(block);
        return info;
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
        {
            _edgeSteps = state.Edges.ToDictionary(e => (e.Branch, e.Operand), e => e.Steps);
            foreach (var step in state.Steps)
            {
                if (step.Action is not null) step.Action(this);
                else EmitVariant(step.Variant!, Resolve(step.Mem));
            }
            foreach (var (target, snapshot) in state.Snaps)
                _entry[target] = snapshot;
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
        Work Start(int exitMode = 0) =>
            new(this, info, state, pos, itemEnd - 1) { PreferMove = preferMove, ExitMode = exitMode };

        // A branch to a block that starts from a snapshot is placed several ways (see
        // Work.ExitMode): leaving the values where they are, storing them first, and,
        // into a join, also putting block parameters in registers.
        var settleChoices =
            instr.Operands.Any(o => o is BlockTarget t && RoleOf(info.Block, t.Block) == ExitRole.Primary) ? new[] { 0, 1, 2, 3, 4, 5, 6 }
            : instr.Operands.Any(o => o is BlockTarget t && RoleOf(info.Block, t.Block) == ExitRole.Single) ? new[] { 0, 1 }
            : new[] { 0 };

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
            foreach (var settle in settleChoices) Add(Start(settle).Jump(instr));
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
                AluPlans(instr, info, op, a: 2, b: 3, carryIn: 4, commutative, () => Start(), Add);
                break;
            }
            case MOS6502Op.And:
                AluPlans(instr, info, MOS6502Operation.And, a: 1, b: 2, carryIn: -1, commutative: true, () => Start(), Add);
                break;
            case MOS6502Op.Ora:
                AluPlans(instr, info, MOS6502Operation.Ora, a: 1, b: 2, carryIn: -1, commutative: true, () => Start(), Add);
                break;
            case MOS6502Op.Eor:
                AluPlans(instr, info, MOS6502Operation.Eor, a: 1, b: 2, carryIn: -1, commutative: true, () => Start(), Add);
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
                foreach (var settle in settleChoices)
                    foreach (var reg in new[] { A, X, Y })
                        Add(Start(settle).Compare(instr, reg));
                break;

            case MOS6502Op.Load:
                foreach (var reg in new[] { A, X, Y })
                    Add(Start().LoadByte(instr, reg));
                if (instr.Operands[0] is VirtualReg loaded && info.FoldedLoad[info.Index[loaded.Id]] is not null)
                    Add(Start().LeaveInMemory(instr));
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
        private int _snapSig;

        public bool PreferMove { get; init; }

        // How a branch hands over to a block that starts from a snapshot: 0 leaves the
        // values where they are, 1 stores them first (and, into a join, hands over
        // empty registers), 2 and 3 also load block parameters into free registers,
        // in parameter order and in reverse; 4 and 5 do the same and store the live-in
        // values as well; 6 only stores the live-in values.
        public int ExitMode { get; init; }
        public bool SettleExits => ExitMode == 1;

        public Work(BeamPlacer placer, BlockInfo info, State from, int pos, int normPos)
        {
            (_placer, _info, _pos, _normPos) = (placer, info, pos, normPos);
            _f = (ulong[])from.F.Clone();
            _r = (int[])from.R.Clone();
            (_c, _nz, _ptrLo, _ptrHi) = (from.C, from.Nz, from.PtrLo, from.PtrHi);
            _h = (ulong[])from.H.Clone();
            _cost = from.Cost;
            _snapSig = from.SnapSig;
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
                    if (_info.End(i) <= _normPos)
                    {
                        _h[i >> 6] &= ~(1UL << (i & 63));
                        _f[i >> 6] &= ~(1UL << (i & 63));
                    }
            }
            return new State
            {
                R = _r, C = _c, Nz = _nz, PtrLo = _ptrLo, PtrHi = _ptrHi, H = _h, F = _f,
                Cost = _cost, Prev = parent, Steps = [.. _steps], Snaps = [.. _snaps], Edges = [.. _edges], SnapSig = _snapSig,
            };
        }

        // A content that is still needed after the operation, else nothing.
        private int Live(int content) =>
            content >= 0 && content < ImmBase && _info.End(content) <= _normPos ? Empty : content;

        // -- queries --------------------------------------------------------

        private bool InH(int i) => (_h[i >> 6] & (1UL << (i & 63))) != 0;
        private void SetH(int i) => _h[i >> 6] |= 1UL << (i & 63);
        private bool InF(int i) => i >= 0 && i < ImmBase && (_f[i >> 6] & (1UL << (i & 63))) != 0;
        private void SetF(int i) => _f[i >> 6] |= 1UL << (i & 63);
        private int VregOf(int i) => _info.Vreg[i];
        private bool IsRemat(int i) => i >= 0 && i < ImmBase && _placer._remat.ContainsKey(VregOf(i));

        // Whether the value is read by the current operation or later, or flows into
        // a successor at a branch from here on.
        private bool Needed(int content, bool after)
        {
            if (content < 0 || content >= ImmBase) return false;
            if (_runEnd >= 0)
                return _info.LastUse[content] >= _runEnd || _info.ExitUse[content] >= _pos || _pending.Contains(content);
            var use = _info.LastUse[content];
            var read = after || _consumed.Contains(content) ? use > _pos : use >= _pos;
            return read || _info.ExitUse[content] >= _pos;
        }

        // While the zero-page and register moves of a run of copies are placed: the
        // position after the run, and the values the moves still to come read. A value
        // the run has already passed on is not needed any more.
        private int _runEnd = -1;
        private readonly HashSet<int> _pending = [];

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
            {
                if (_info.FoldedLoad[want] is { } global)
                    Consider(MOS6502Variants.Find(MOS6502Operation.Load, RegisterPlace(register), Place.Abs), MemRef.Sym(global));
                else
                    Consider(MOS6502Variants.Find(MOS6502Operation.Reload, RegisterPlace(register), Place.Home),
                        MemRef.Phys(_info.FixedReg[want]));
            }
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
                if (_info.FoldedLoad[content] is { } global) (place, mem) = (Place.Abs, MemRef.Sym(global));
                else (place, mem) = (Place.Home, MemRef.Phys(_info.FixedReg[content]));
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

            if (isBranch && !StoreForExits(instr, pinned: 1 << reg)) return null;

            // The compare is emitted either way; when N/Z already reflect the value the
            // late optimisation drops it, so the search does not pay for it.
            var redundant = zeroTest && _nz == left && _r[reg] == left;
            var before = _cost;
            Do(V(MOS6502Operation.Cmp, RegisterPlace(reg), place), memRef);
            if (redundant) _cost = before;
            _nz = Empty;
            _c = Empty;

            if (isBranch)
            {
                CaptureExits(instr);
                if (!PlaceJoinExits(instr)) return null;
                Do(placer => placer.EmitCompareBranches(instr));
            }
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

        // %v = load @sym+off, left in memory: its uses read the global itself.
        public Work? LeaveInMemory(MirInstruction instr)
        {
            SetF(ResultIndex(instr.Operands[0]));
            return this;
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

        // Stores every value the rest of the block, or a successor, still needs;
        // `needFrom` names the position from which a use counts (the barrier after a
        // run of copies); `pinned` registers are left alone.
        public bool StoreLive(int pinned = 0, int needFrom = int.MinValue, int defLimit = int.MinValue)
        {
            var limit = defLimit == int.MinValue ? _pos : defLimit;
            var from = needFrom == int.MinValue ? _pos : needFrom;
            var live = new List<int>();
            for (var i = 0; i < _info.Count; i++)
                if (_info.DefPos[i] < limit && !InH(i) && _info.End(i) >= from)
                    live.Add(i);
            return Store(live, pinned);
        }

        // Stores what the targets of a branch need to find in memory: for a successor
        // that starts from empty registers, every value live into it and the
        // arguments it takes; for one that inherits the registers (its only
        // incoming edge), only a value that lives in a zero-page argument register
        // (it has no home to inherit). The other edges into a join do their own
        // storing, after the branch (DefineEntry, SatisfyEntry).
        public bool StoreForExits(MirInstruction branch, int pinned = 0, bool includeArguments = true)
        {
            var live = new HashSet<int>();
            foreach (var operand in branch.Operands)
            {
                if (operand is not BlockTarget target) continue;
                var role = _placer.RoleOf(_info.Block, target.Block);
                if (role is ExitRole.Primary or ExitRole.Secondary) continue;
                var inheriting = role == ExitRole.Single;
                foreach (var vreg in _placer._liveIn[target.Block])
                    if (_info.Index.TryGetValue(vreg, out var i) && (!inheriting || SettleExits || InF(i)))
                        live.Add(i);
                if (inheriting || !includeArguments) continue;
                foreach (var arg in target.Args)
                    if (arg is VirtualReg v && _info.Index.TryGetValue(v.Id, out var index))
                        live.Add(index);
            }
            return Store(live.Where(i => _info.DefPos[i] < _pos && !InH(i)).OrderBy(i => i).ToList(), pinned);
        }

        private bool Store(List<int> live, int pinned)
        {
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

        // A block that is nothing but the jump disappears in branch folding; one
        // with code before it keeps the JMP (3 bytes, 3 cycles) unless it falls through.
        private void AddJumpCost()
        {
            if (_cost > 0)
                _cost += 3L * _placer.Options.SizeWeight + 3L * _placer.Options.SpeedWeight * _info.Frequency;
        }

        // What the registers hold on the way out to each successor that inherits them.
        private readonly List<(MirBlock Target, Snapshot Snapshot)> _snaps = [];
        private readonly List<(MirInstruction Branch, int Operand, Step[] Steps)> _edges = [];

        private int ToVreg(int content) => content >= 0 && content < ImmBase ? _info.Vreg[content] : content;

        private void Hand(MirBlock target, Snapshot snapshot)
        {
            _snaps.Add((target, snapshot));
            var hash = new HashCode();
            hash.Add(_snapSig); hash.Add(target.GetHashCode());
            foreach (var content in snapshot.R) hash.Add(content);
            hash.Add(snapshot.C); hash.Add(snapshot.PtrLo); hash.Add(snapshot.PtrHi);
            foreach (var vreg in snapshot.Homed.Order()) hash.Add(vreg);
            _snapSig = hash.ToHashCode();
        }

        // The registers as they are, for the only edge into a block.
        private void CaptureExits(MirInstruction branch)
        {
            foreach (var operand in branch.Operands)
            {
                if (operand is not BlockTarget target || _placer.RoleOf(_info.Block, target.Block) != ExitRole.Single) continue;
                var homed = new HashSet<int>();
                for (var i = 0; i < _info.Count; i++)
                    if (InH(i)) homed.Add(_info.Vreg[i]);
                Hand(target.Block, new Snapshot
                {
                    R = [ToVreg(_r[A]), ToVreg(_r[X]), ToVreg(_r[Y])],
                    C = ToVreg(_c), PtrLo = ToVreg(_ptrLo), PtrHi = ToVreg(_ptrHi), Homed = homed,
                });
            }
        }

        // A copy of this work to place the code of one edge in: it starts at zero
        // cost and has steps of its own.
        private Work Fork()
        {
            var state = new State
            {
                R = (int[])_r.Clone(), C = _c, Nz = _nz, PtrLo = _ptrLo, PtrHi = _ptrHi,
                H = (ulong[])_h.Clone(), F = (ulong[])_f.Clone(),
            };
            return new Work(_placer, _info, state, _pos, _normPos) { PreferMove = PreferMove, ExitMode = ExitMode };
        }

        // The exits of a cmp_br into joins: each is placed on a copy of the work, the
        // steps going into a block on that edge, the cost counted here.
        private bool PlaceJoinExits(MirInstruction branch)
        {
            for (var k = 2; k < branch.Operands.Length; k += 2)
            {
                var target = (BlockTarget)branch.Operands[k + 1];
                var role = _placer.RoleOf(_info.Block, target.Block);
                if (role is not (ExitRole.Primary or ExitRole.Secondary)) continue;

                var edge = Fork();
                if (!(role == ExitRole.Primary ? edge.DefineEntry(target, ExitMode) : edge.SatisfyEntry(target)))
                    return false;
                _cost += edge._cost;
                _edges.Add((branch, k + 1, [.. edge._steps]));
                foreach (var (block, snapshot) in edge._snaps) Hand(block, snapshot);
            }
            return true;
        }

        // The values live into a join and the parameters it takes that are used, as
        // block-local indices (-1 for an argument that is not a vreg of this block).
        private IEnumerable<int> LiveInto(BlockTarget target) =>
            _placer._liveIn[target.Block].Where(_info.Index.ContainsKey).Select(v => _info.Index[v]).Order();

        // The first edge into a join: its registers become the state the join starts
        // from, after storing what has to be in memory. A block parameter whose
        // argument is in a register (and is not itself live into the join) stays
        // there under the parameter's name; the others are moved into their homes.
        // `settle` stores everything and hands over empty registers instead.
        private bool DefineEntry(BlockTarget target, int mode)
        {
            var settle = mode == 1;
            var homeLive = mode >= 4;
            var parameters = target.Block.Parameters;
            var moves = _placer.EdgeMoves(target);
            var hazard = _placer.HasMoveHazard(moves);
            if (hazard && !settle) return false;

            // Which register keeps which parameter (and which value of this block it
            // holds, to check it is still there once the code for the rest is placed).
            var named = new int[3] { Empty, Empty, Empty };
            var namedValue = new int[3] { Empty, Empty, Empty };
            var claimed = new HashSet<int>();
            if (!settle)
                for (var k = 0; k < parameters.Count; k++)
                {
                    if (_placer._uses.GetValueOrDefault(parameters[k]) == 0) continue;
                    if (target.Args[k] is not VirtualReg { IsDefinition: false } arg || _placer._constants.ContainsKey(arg.Id)) continue;
                    if (_placer._liveIn[target.Block].Contains(arg.Id) || !_info.Index.TryGetValue(arg.Id, out var index)) continue;
                    if (_placer._remat.ContainsKey(arg.Id)) continue;
                    var register = FindRegister(index);
                    if (register < 0 || named[register] != Empty || claimed.Contains(arg.Id)) continue;
                    named[register] = parameters[k];
                    namedValue[register] = index;
                    claimed.Add(arg.Id);
                }

            // Block parameters whose argument is not in a register can be loaded into a
            // register that holds nothing the join needs.
            if (mode is >= 2 and <= 5)
            {
                var order = parameters.Where(p => _placer._uses.GetValueOrDefault(p) > 0 && !named.Contains(p)).ToList();
                if (mode is 3 or 5) order.Reverse();
                foreach (var parameter in order)
                {
                    var k = parameters.IndexOf(parameter);
                    var source = target.Args[k];
                    if (source is VirtualReg { IsDefinition: false } sv && (_placer._remat.ContainsKey(sv.Id)
                        || _placer._liveIn[target.Block].Contains(sv.Id))) continue;
                    var pinned = 0;
                    for (var r = 0; r < 3; r++) if (named[r] != Empty) pinned |= 1 << r;
                    var register = new[] { X, Y, A }.FirstOrDefault(r => (pinned & (1 << r)) == 0 && IsFree(r) && !LiveRegister(r, target), -1);
                    if (register < 0) break;
                    var content = ContentOf(source);
                    if (!PutIn(register, content, pinned)) return false;
                    named[register] = parameter;
                    namedValue[register] = content;
                }
            }

            // Live-in values are in a register or in their home.
            foreach (var i in LiveInto(target))
            {
                if (_info.DefPos[i] >= _pos || IsRemat(i)) continue;
                var inRegister = !settle && !homeLive && FindRegister(i) >= 0;
                if (!inRegister && !InH(i) && !EnsureHome(i, 0)) return false;
            }

            // Parameters not kept in a register are written to their homes. Placing
            // those moves can displace a register that was to keep a parameter; such a
            // parameter is moved after all.
            if (hazard)
            {
                foreach (var arg in target.Args)
                    if (arg is VirtualReg v && _info.Index.TryGetValue(v.Id, out var index) && !IsRemat(index)
                        && !InH(index) && !EnsureHome(index, 0))
                        return false;
                Do(placer => placer.EmitEdgeMoves(target));
                _r[A] = Empty;
                _nz = Empty;
                _c = Empty;
            }
            else
            {
                var kept = named.Where(n => n != Empty).ToHashSet();
                foreach (var (parameter, source) in moves)
                    if (!kept.Contains(parameter) && !MoveToParameter(parameter, source))
                        return false;
                bool displaced;
                do
                {
                    displaced = false;
                    for (var register = 0; register < 3; register++)
                    {
                        if (named[register] == Empty || _r[register] == namedValue[register]) continue;
                        var parameter = named[register];
                        named[register] = Empty;
                        displaced = true;
                        var move = moves.FirstOrDefault(m => m.Parameter == parameter);
                        if (move.Source is not null && !MoveToParameter(parameter, move.Source)) return false;
                    }
                } while (displaced);
            }
            var keptParameters = named.Where(n => n != Empty).ToHashSet();

            // Shared homes: the argument has to be in the home it shares.
            for (var k = 0; k < parameters.Count; k++)
            {
                if (keptParameters.Contains(parameters[k]) || _placer._uses.GetValueOrDefault(parameters[k]) == 0) continue;
                if (moves.Any(m => m.Parameter == parameters[k])) continue;
                if (target.Args[k] is VirtualReg { IsDefinition: false } shared && !_placer._constants.ContainsKey(shared.Id))
                {
                    var content = ContentOf(shared);
                    if (!InH(content) && !IsRemat(content) && !EnsureHome(content, 0)) return false;
                }
            }

            var homed = new HashSet<int>();
            var registers = new int[3];
            for (var k = 0; k < 3; k++)
            {
                var content = _r[k];
                registers[k] = settle ? Empty
                    : named[k] != Empty ? named[k]
                    : content >= 0 && content < ImmBase && _placer._liveIn[target.Block].Contains(_info.Vreg[content])
                        && !(homeLive && InH(content)) ? _info.Vreg[content]
                    : Empty;
            }
            var inRegisters = registers.Where(r => r != Empty).ToHashSet();
            foreach (var i in LiveInto(target))
                if (_info.DefPos[i] < _pos && InH(i) && (homeLive || !inRegisters.Contains(_info.Vreg[i]))) homed.Add(_info.Vreg[i]);
            foreach (var parameter in parameters)
                if (_placer._uses.GetValueOrDefault(parameter) > 0 && !inRegisters.Contains(parameter)) homed.Add(parameter);

            Hand(target.Block, new Snapshot { R = registers, C = Empty, PtrLo = Empty, PtrHi = Empty, Homed = homed });
            return true;
        }

        // Whether the register holds a value the join needs and that has no home.
        private bool LiveRegister(int register, BlockTarget target)
        {
            var content = _r[register];
            return content >= 0 && content < ImmBase && !InH(content)
                && _placer._liveIn[target.Block].Contains(_info.Vreg[content]);
        }

        // A later edge into a join: the values it starts from, in the registers and
        // the homes the first edge left them in.
        private bool SatisfyEntry(BlockTarget target)
        {
            var contract = _placer.Contract(target.Block);
            if (contract is null)
            {
                // Looking ahead from the join's first edge: the state it starts from is
                // being judged, so the edge pays for reaching that one (a loop's back edge
                // is part of what a header's state costs).
                if (_placer._lookingAhead > 0)
                {
                    if (!_placer._provisional.TryGetValue(target.Block, out contract)) return true;
                }
                else throw new InvalidOperationException(
                    $"MOS6502BeamPlacement: an edge into a join of @{_placer.Function.Name} is placed before the join's state is known.");
            }
            var parameters = target.Block.Parameters;

            // The content (a block-local index or a known byte) the join expects under a name.
            int Source(int name)
            {
                var k = parameters.IndexOf(name);
                if (k >= 0) return ContentOf(target.Args[k]);
                return _info.Index.TryGetValue(name, out var index) ? index : Empty;
            }

            var moves = _placer.EdgeMoves(target);
            var hazard = _placer.HasMoveHazard(moves);

            if (hazard)
            {
                // Everything is read back from its home, so everything has to be in one.
                foreach (var i in LiveInto(target))
                    if (_info.DefPos[i] < _pos && !IsRemat(i) && !InH(i) && !EnsureHome(i, 0)) return false;
                foreach (var arg in target.Args)
                    if (arg is VirtualReg v && _info.Index.TryGetValue(v.Id, out var index) && !IsRemat(index)
                        && !InH(index) && !EnsureHome(index, 0))
                        return false;
                Do(placer => placer.EmitEdgeMoves(target));
                _r[A] = Empty;
                _nz = Empty;
                _c = Empty;
            }
            else
            {
                foreach (var name in contract.Homed.Order())
                {
                    if (parameters.Contains(name))
                    {
                        var move = moves.FirstOrDefault(m => m.Parameter == name);
                        if (move.Source is not null) { if (!MoveToParameter(name, move.Source)) return false; continue; }
                        var shared = Source(name);
                        if (shared >= 0 && shared < ImmBase && !InH(shared) && !IsRemat(shared) && !EnsureHome(shared, 0)) return false;
                        continue;
                    }
                    var value = Source(name);
                    if (value >= 0 && value < ImmBase && !InH(value) && !IsRemat(value) && !EnsureHome(value, 0)) return false;
                }
            }

            var pinned = 0;
            foreach (var register in new[] { Y, X, A })
            {
                var name = contract.R[register];
                if (name == Empty) continue;
                var content = Source(name);
                if (content == Empty || !PutIn(register, content, pinned)) return false;
                pinned |= 1 << register;
            }
            return true;
        }

        // The home of a parameter <- its argument, from wherever the argument is.
        private bool MoveToParameter(int parameter, MirOperand source)
        {
            var content = ContentOf(source);
            if (!IsImm(content) && !InH(content) && !InF(content) && FindRegister(content) < 0
                && !IsRemat(content) && !EnsureHome(content, 0))
                return false;
            var register = FindRegister(content);
            if (register < 0)
            {
                register = PickScratch(0);
                if (!PutIn(register, content)) return false;
            }
            Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home), MemRef.HomeOf(parameter));
            return true;
        }

        // cf.br: the arguments go straight from where they are into the parameters'
        // homes. A branch whose moves read a parameter another move writes keeps the
        // naive parallel move, from the arguments' homes.
        public Work? Jump(MirInstruction instr)
        {
            var target = (BlockTarget)instr.Operands[0];

            var role = _placer.RoleOf(_info.Block, target.Block);
            if (role is ExitRole.Primary or ExitRole.Secondary)
            {
                if (!(role == ExitRole.Primary ? DefineEntry(target, ExitMode) : SatisfyEntry(target))) return null;
                AddJumpCost();
                Do(placer => placer.Emit(MOS6502Op.JmpAbs, new BlockTarget(target.Block, [])));
                return this;
            }

            var moves = _placer.EdgeMoves(target);
            var hazard = _placer.HasMoveHazard(moves);
            if (hazard)
            {
                if (!StoreForExits(instr)) return null;
                Do(placer => placer.EmitEdgeMoves(target));
            }
            else
            {
                if (!StoreForExits(instr, includeArguments: false)) return null;

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
            CaptureExits(instr);
            AddJumpCost();
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

                // A copy between values that share a home is a rename: the value is
                // in its home if the source was.
                if (source >= 0 && source < ImmBase && InH(source)
                    && _placer.HomeKey(VregOf(source)) == _placer.HomeKey(((VirtualReg)copy.Operands[0]).Id))
                {
                    var renamed = ResultIndex(copy.Operands[0]);
                    SetH(renamed);
                    if (register >= 0) _r[register] = renamed;
                    continue;
                }

                if (register < 0)
                {
                    register = PickScratch(0);
                    if (!PutIn(register, source)) return null;
                }
                SaveRegister(register, after: true);
                _r[register] = ResultIndex(copy.Operands[0]);
                _nz = Empty;
            }

            // An argument passed on in the register it arrived in is already there.
            var inPlace = toZp.Where(c =>
            {
                var source = ContentOf(c.Operands[1]);
                var written = ((PhysicalReg)c.Operands[0]).Id;
                if (source < 0 || IsImm(source)) return false;
                if (_placer.TryFixedHome(VregOf(source), out var home)) return InH(source) && home == written;
                return InF(source) && !InH(source) && _info.FixedReg[source] == written;
            }).ToHashSet();
            var setsUp = toZp.Count > 0 || toGpr.Any(l => l.Count > 0);
            if (setsUp)
            {
                if (!StoreLive(needFrom: nextPos, defLimit: nextPos)) return null;
                if (toZp.Count > 0)
                {
                    // An argument still in the register it arrived in moves home only
                    // if it is needed after the run, or if the run reads it after a
                    // copy into its register that does not itself pass it on.
                    var read = new HashSet<int>();
                    foreach (var copy in run)
                        if (copy.Operands[1] is VirtualReg r && _info.Index.TryGetValue(r.Id, out var ri)) read.Add(ri);
                    for (var i = 0; i < _info.Count; i++)
                    {
                        if (!InF(i) || InH(i)) continue;
                        var clobbered = read.Contains(i) && toZp.Any(c =>
                            ((PhysicalReg)c.Operands[0]).Id == _info.FixedReg[i]
                            && !(c.Operands[1] is VirtualReg s && _info.Index.TryGetValue(s.Id, out var si) && si == i));
                        if ((_info.End(i) >= nextPos || clobbered) && !EnsureHome(i, 0)) return null;
                    }
                    foreach (var copy in toZp)
                    {
                        if (inPlace.Contains(copy)) continue;
                        var written = ((PhysicalReg)copy.Operands[0]).Id;
                        for (var i = 0; i < _info.Count; i++)
                            if (_info.FixedReg[i] == written) _f[i >> 6] &= ~(1UL << (i & 63));
                    }
                }
            }

            var moves = toZp.Concat(toGpr[Y]).Concat(toGpr[X]).Concat(toGpr[A]).ToList();
            void Pending(int from)
            {
                _pending.Clear();
                for (var k = from; k < moves.Count; k++)
                    if (!inPlace.Contains(moves[k])) _pending.Add(ContentOf(moves[k].Operands[1]));
            }
            _runEnd = nextPos;
            Pending(0);

            var done = 0;
            foreach (var copy in toZp)
            {
                var source = ContentOf(copy.Operands[1]);
                if (!inPlace.Contains(copy))
                {
                    var register = FindRegister(source);
                    if (register < 0)
                    {
                        register = PickScratch(0);
                        if (!PutIn(register, source)) return null;
                    }
                    Do(V(MOS6502Operation.Spill, RegisterPlace(register), Place.Home),
                        MemRef.Phys(((PhysicalReg)copy.Operands[0]).Id));
                }
                Pending(++done);
            }

            var pinned = 0;
            foreach (var register in new[] { Y, X, A })
            {
                foreach (var copy in toGpr[register])
                {
                    if (!PutIn(register, ContentOf(copy.Operands[1]), pinned)) return null;
                    Pending(++done);
                }
                if (toGpr[register].Count > 0) pinned |= 1 << register;
            }
            _runEnd = -1;
            return this;
        }
    }

    // The branches of a cmp_br after its compare: one per exit, through a block of
    // moves when the exit passes arguments.
    internal void EmitCompareBranches(MirInstruction instr) => EmitBranches(instr);

    // The code of the edges out of the branch being replayed, by operand.
    private Dictionary<(MirInstruction Branch, int Operand), Step[]> _edgeSteps = [];

    internal override BlockTarget ExitTarget(MirInstruction instr, int operandIndex, BlockTarget target)
    {
        if (!_edgeSteps.TryGetValue((instr, operandIndex), out var steps))
            return EdgeTarget(target);
        if (steps.Length == 0)
            return new BlockTarget(target.Block, []);

        var saved = (_out, _carryHolds);
        _out = [];
        _carryHolds = -1;
        foreach (var step in steps)
        {
            if (step.Action is not null) step.Action(this);
            else EmitVariant(step.Variant!, Resolve(step.Mem));
        }
        Emit(MOS6502Op.JmpAbs, new BlockTarget(target.Block, []));

        var edge = Function.CreateBlock();
        _edgeBlocks.Add(edge);
        foreach (var instr2 in _out)
        {
            instr2.Parent = edge;
            edge.Instructions.Add(instr2);
        }
        (_out, _carryHolds) = saved;
        return new BlockTarget(edge, []);
    }
}
