using Irie.Dialects.Cf;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;

namespace Irie.Target.MOS6502;

// The trivial placement of the unpinned operations MOS6502ShapeSelector emits:
// a search that keeps one state (K = 1) and homes everything.
//
// Every non-constant byte value, and every block parameter, gets a HOME: a
// function-local zero-page byte ($h0, $h1, … until program layout gives each an
// address, in zero page or in absolute memory). An operation reads its operands
// from their homes (or takes a constant as an immediate), computes in A, and
// writes its result to the result's home, choosing from MOS6502Variants the
// variant that does this with A as the only scratch register. So the registers
// are empty between operations and nothing is ever live in A, X, Y or a flag
// across them. That is correct for any input, which is the point: the code is
// naive, it exists so correctness lands before the placement search of the later
// stages, and it is the K = 1 baseline of that search.
//
// One thing is not homed. A carry that is produced by one operation and consumed
// by the very next one (the links of a multi-byte add or shift) stays in C; the
// pass tracks that the flag still holds it. Any other carry is stored to its home
// as 0 / 1 and reloaded into C when needed.
//
// Block parameters are not copies in the IR any more: a branch writes the
// arguments to the parameters' homes (a parallel move, through temporary homes if
// one argument is itself a parameter being written). A conditional exit that
// passes arguments leaves through a new block that does the moves.
//
// The output is physreg-only MIR in the concrete, pinned ops, ready for the tail
// of the pipeline (frame lowering, recursion saves, program layout, pseudo
// expansion, …).
public sealed class MOS6502NaivePlacementPass : MirFunctionPass
{
    public override string Name => "MOS6502NaivePlacement";

    public override void Run(MirFunction function) => new Placer(function).Run();

    // The placer is shared with MOS6502BeamPlacementPass, which replaces how the
    // operations of a block are placed and reuses everything else (copies, edges,
    // live-ins).
    internal class Placer(MirFunction function)
    {
        internal MirFunction Function => function;
        internal const int A = MOS6502Registers.A;
        internal const int X = MOS6502Registers.X;
        internal const int Y = MOS6502Registers.Y;
        internal const int C = MOS6502Registers.C;

        internal readonly Dictionary<int, long> _constants = [];
        internal readonly Dictionary<int, int> _uses = [];
        internal readonly Dictionary<int, int> _homes = [];
        internal int _nextHome;

        // Address bytes (`const @sym, half`), which cost one load wherever they are
        // needed; the vregs live into and out of each block; the blocks that only move
        // a conditional exit's arguments.
        internal readonly Dictionary<int, (Symbol Symbol, long Half)> _remat = [];
        internal readonly Dictionary<MirBlock, HashSet<int>> _liveIn = [];
        internal readonly Dictionary<MirBlock, HashSet<int>> _liveOut = [];
        internal readonly HashSet<MirBlock> _edgeBlocks = [];

        // The instructions of the block being placed, and the vreg whose value the
        // carry flag is known to hold right now (-1: none).
        internal List<MirInstruction> _out = [];
        internal int _carryHolds = -1;

        public void Run()
        {
            foreach (var block in function.Blocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (IsOp(instr, MOS6502Op.Const) && instr.Operands[1] is Immediate imm)
                        _constants[((VirtualReg)instr.Operands[0]).Id] = imm.Value;
                    else if (IsOp(instr, MOS6502Op.Const) && instr.Operands[1] is Symbol symbol)
                        _remat[((VirtualReg)instr.Operands[0]).Id] = (symbol, ((Immediate)instr.Operands[2]).Value);
                    foreach (var operand in instr.Operands)
                        CountUse(operand);
                }
            }

            ComputeLiveness();
            BeforePlacement();
            foreach (var block in function.Blocks.ToList())
                PlaceBlock(block);

            // The parameters are read by every branch to the block, so they go last.
            foreach (var block in function.Blocks)
                block.Parameters.Clear();

            function.ClearVRegAnnotations();
            function.RebuildCfg();
            RecomputeLiveIns();
        }

        // ------------------------------------------------------------------
        // Liveness
        // ------------------------------------------------------------------

        // Vregs that need a home: everything but the immediate constants.
        internal bool NeedsHome(int vreg) => !_constants.ContainsKey(vreg);

        internal IEnumerable<VirtualReg> Uses(MirInstruction instr)
        {
            foreach (var operand in instr.Operands)
            {
                if (operand is VirtualReg { IsDefinition: false } use && NeedsHome(use.Id))
                    yield return use;
                else if (operand is BlockTarget target)
                    foreach (var arg in target.Args)
                        if (arg is VirtualReg { IsDefinition: false } argument && NeedsHome(argument.Id))
                            yield return argument;
            }
        }

        internal IEnumerable<VirtualReg> Defs(MirInstruction instr) =>
            instr.Operands.OfType<VirtualReg>().Where(v => v.IsDefinition && NeedsHome(v.Id));

        internal void ComputeLiveness()
        {
            function.RebuildCfg();
            var gen = new Dictionary<MirBlock, HashSet<int>>();
            var kill = new Dictionary<MirBlock, HashSet<int>>();
            foreach (var block in function.Blocks)
            {
                var defined = block.Parameters.Where(NeedsHome).ToHashSet();
                var used = new HashSet<int>();
                foreach (var instr in block.Instructions)
                {
                    foreach (var use in Uses(instr))
                        if (!defined.Contains(use.Id)) used.Add(use.Id);
                    foreach (var def in Defs(instr))
                        defined.Add(def.Id);
                }
                gen[block] = used;
                kill[block] = defined;
                _liveIn[block] = [];
                _liveOut[block] = [];
            }

            bool changed;
            do
            {
                changed = false;
                for (var i = function.Blocks.Count - 1; i >= 0; i--)
                {
                    var block = function.Blocks[i];
                    var live = new HashSet<int>();
                    foreach (var successor in block.Successors)
                        live.UnionWith(_liveIn[successor]);
                    _liveOut[block] = live;

                    var liveIn = new HashSet<int>(live);
                    liveIn.ExceptWith(kill[block]);
                    liveIn.UnionWith(gen[block]);
                    if (!liveIn.SetEquals(_liveIn[block]))
                    {
                        _liveIn[block] = liveIn;
                        changed = true;
                    }
                }
            } while (changed);
        }

        // Called once the constants and use counts are known, before any block is placed.
        internal virtual void BeforePlacement() { }

        internal void CountUse(MirOperand operand)
        {
            switch (operand)
            {
                case VirtualReg { IsDefinition: false } use:
                    _uses[use.Id] = _uses.GetValueOrDefault(use.Id) + 1;
                    break;
                case BlockTarget target:
                    foreach (var arg in target.Args)
                        CountUse(arg);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Walking a block
        // ------------------------------------------------------------------

        internal virtual void PlaceBlock(MirBlock block)
        {
            _out = [];
            _carryHolds = -1;

            var instrs = block.Instructions.ToList();
            for (var i = 0; i < instrs.Count;)
                i = PlaceInstruction(instrs, i);

            CheckBlock(block, instrs);
            block.Instructions.Clear();
            foreach (var instr in _out)
            {
                instr.Parent = block;
                block.Instructions.Add(instr);
            }
        }

        // Whether the code placed for each block is checked against the block it came
        // from (PlacementChecker); IRIE_NO_PLACEMENT_CHECK turns it off.
        internal static readonly bool CheckPlacement =
            Environment.GetEnvironmentVariable("IRIE_NO_PLACEMENT_CHECK") is null;

        internal void CheckBlock(MirBlock block, List<MirInstruction> before)
        {
            if (CheckPlacement) new PlacementChecker(this).Check(block, before, _out);
        }

        // Places instrs[i] (or the run of copies starting there); returns the index
        // of the next instruction to place.
        internal int PlaceInstruction(List<MirInstruction> instrs, int i)
        {
            var instr = instrs[i];
            var opcode = instr.Opcode;

            if (opcode.Dialect == PseudoDialect.Id)
            {
                switch ((PseudoOp)opcode.Code)
                {
                    case PseudoOp.Copy:
                        var end = i;
                        while (end < instrs.Count && IsPseudo(instrs[end], PseudoOp.Copy)) end++;
                        PlaceCopies(instrs.GetRange(i, end - i));
                        return end;
                    case PseudoOp.Merge when Unused(instr.Operands[0]):
                        return i + 1;
                }
                throw Unsupported(instr);
            }

            if (opcode.Dialect == CfDialect.Id && (CfOp)opcode.Code == CfOp.Br)
            {
                var target = (BlockTarget)instr.Operands[0];
                EmitEdgeMoves(target);
                Emit(MOS6502Op.JmpAbs, new BlockTarget(target.Block, []));
                return i + 1;
            }

            if (opcode.Dialect == MemDialect.Id && (MemOp)opcode.Code == MemOp.Symbol && Unused(instr.Operands[0]))
                return i + 1;

            if (opcode.Dialect != MOS6502Dialect.Id)
                throw Unsupported(instr);

            switch ((MOS6502Op)opcode.Code)
            {
                case MOS6502Op.Const:   PlaceConst(instr); break;
                case MOS6502Op.Adc:     PlaceAddSub(instr, MOS6502Operation.Adc, instrs, i); break;
                case MOS6502Op.Sbc:     PlaceAddSub(instr, MOS6502Operation.Sbc, instrs, i); break;
                case MOS6502Op.Inc:     PlaceStep(instr, MOS6502Operation.Adc, "clc"); break;
                case MOS6502Op.Dec:     PlaceStep(instr, MOS6502Operation.Sbc, "sec"); break;
                case MOS6502Op.And:     PlaceLogic(instr, MOS6502Operation.And); break;
                case MOS6502Op.Ora:     PlaceLogic(instr, MOS6502Operation.Ora); break;
                case MOS6502Op.Eor:     PlaceLogic(instr, MOS6502Operation.Eor); break;
                case MOS6502Op.Shle:    PlaceShift(instr, MOS6502Operation.Shl, instrs, i); break;
                case MOS6502Op.Lshre:   PlaceShift(instr, MOS6502Operation.Lshr, instrs, i); break;
                case MOS6502Op.CmpBr:   PlaceCmpBr(instr); break;
                case MOS6502Op.Cmp:     PlaceCmp(instr, instrs, i); break;
                case MOS6502Op.Load:    PlaceLoad(instr); break;
                case MOS6502Op.Store:   PlaceStore(instr); break;

                case MOS6502Op.JsrAbs:
                case MOS6502Op.Rts:
                    Emit((MOS6502Op)opcode.Code, instr.Operands);
                    break;
                case MOS6502Op.FrameLoadByte:  PlaceFrameLoad(instr); break;
                case MOS6502Op.FrameStoreByte: PlaceFrameStore(instr); break;

                // Hand-written code in the runtime is already in physical registers.
                case var _ when instr.Operands.All(o => o is not VirtualReg):
                    Emit((MOS6502Op)opcode.Code, instr.Operands);
                    break;

                default:
                    throw Unsupported(instr);
            }
            return i + 1;
        }

        internal static bool IsOp(MirInstruction instr, MOS6502Op op) =>
            instr.Opcode.Dialect == MOS6502Dialect.Id && (MOS6502Op)instr.Opcode.Code == op;

        internal static bool IsPseudo(MirInstruction instr, PseudoOp op) =>
            instr.Opcode.Dialect == PseudoDialect.Id && (PseudoOp)instr.Opcode.Code == op;

        internal bool Unused(MirOperand def) =>
            def is VirtualReg v && _uses.GetValueOrDefault(v.Id) == 0;

        internal static NotSupportedException Unsupported(MirInstruction instr)
        {
            var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
            return new NotSupportedException(
                $"MOS6502NaivePlacement: no placement for {dialect.Prefix}.{dialect.GetOpName(instr.Opcode.Code)}.");
        }

        // ------------------------------------------------------------------
        // Emitting
        // ------------------------------------------------------------------

        internal MirInstruction Emit(MOS6502Op op, params MirOperand[] operands)
        {
            var instr = new MirInstruction(MOS6502Dialect.OpRef(op), operands);
            _out.Add(instr);
            if (WritesCarry(op))
                _carryHolds = -1;
            return instr;
        }

        internal void EmitVariant(Variant variant, MirOperand? memory = null)
        {
            foreach (var (opcode, operands) in variant.Instantiate(memory))
                Emit((MOS6502Op)opcode.Code, operands);
        }

        // Anything that may change C: the ALU and shift ops, compares, the flag ops
        // and everything that calls out (a call, the expansion of a frame access).
        internal static bool WritesCarry(MOS6502Op op) => op is
            MOS6502Op.AdcImm or MOS6502Op.AdcZp or MOS6502Op.AdcAbs
            or MOS6502Op.SbcImm or MOS6502Op.SbcZp or MOS6502Op.SbcAbs
            or MOS6502Op.CmpImm or MOS6502Op.CmpZp or MOS6502Op.CmpAbs
            or MOS6502Op.CpxImm or MOS6502Op.CpxZp or MOS6502Op.CpxAbs
            or MOS6502Op.CpyImm or MOS6502Op.CpyZp or MOS6502Op.CpyAbs
            or MOS6502Op.AslAcc or MOS6502Op.LsrAcc or MOS6502Op.RolAcc or MOS6502Op.RorAcc
            or MOS6502Op.Clc or MOS6502Op.Sec
            or MOS6502Op.JsrAbs or MOS6502Op.FrameLoadByte or MOS6502Op.FrameStoreByte;

        // ------------------------------------------------------------------
        // Values: constants, homes, registers
        // ------------------------------------------------------------------

        internal bool TryConst(MirOperand operand, out long value)
        {
            switch (operand)
            {
                case Immediate imm:
                    value = imm.Value;
                    return true;
                case VirtualReg { IsDefinition: false } v when _constants.TryGetValue(v.Id, out value):
                    return true;
            }
            value = 0;
            return false;
        }

        // Which values share a home: the key a value's home is filed under. Every
        // value has a home of its own here; the beam placement overrides this to
        // let values that are never live together share one.
        internal virtual int HomeKey(int vreg) => vreg;

        internal virtual PhysicalReg Home(int vreg)
        {
            var key = HomeKey(vreg);
            if (!_homes.TryGetValue(key, out var n))
                _homes[key] = n = _nextHome++;
            return new PhysicalReg(MOS6502Registers.Home(n), IsDefinition: false);
        }

        // The register of the home a value already has, without giving it one.
        internal virtual bool TryGetHome(int vreg, out int register)
        {
            if (_homes.TryGetValue(HomeKey(vreg), out var n))
            {
                register = MOS6502Registers.Home(n);
                return true;
            }
            register = 0;
            return false;
        }

        internal PhysicalReg NewTemporaryHome() =>
            new(MOS6502Registers.Home(_nextHome++), IsDefinition: false);

        internal static Place RegisterPlace(int reg) => reg switch
        {
            A => Place.A,
            X => Place.X,
            Y => Place.Y,
            _ => throw new ArgumentOutOfRangeException(nameof(reg)),
        };

        // register <- the value of `operand` (an immediate, or its home).
        internal void LoadRegister(int reg, MirOperand operand)
        {
            if (TryConst(operand, out var value))
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Const, RegisterPlace(reg), Place.Imm, "imm"),
                    new Immediate(value));
            else if (operand is VirtualReg { IsDefinition: false } v)
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Reload, RegisterPlace(reg), Place.Home),
                    Home(v.Id));
            else
                throw new NotSupportedException($"MOS6502NaivePlacement: cannot load operand {operand}.");
        }

        // The memory-side operand of an ALU or compare variant, and its place.
        internal (Place Place, MirOperand Operand) MemorySide(MirOperand operand) =>
            TryConst(operand, out var value)
                ? (Place.Imm, new Immediate(value))
                : (Place.Home, Home(((VirtualReg)operand).Id));

        // home of `def` <- register, unless nothing reads it.
        internal void StoreResult(MirOperand def, int reg)
        {
            var vreg = ((VirtualReg)def).Id;
            if (_uses.GetValueOrDefault(vreg) == 0) return;
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Spill, RegisterPlace(reg), Place.Home), Home(vreg));
        }

        // ------------------------------------------------------------------
        // The carry
        // ------------------------------------------------------------------

        // Puts the carry-in operand into C.
        internal void EnsureCarry(MirOperand carryIn)
        {
            if (TryConst(carryIn, out var value))
            {
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Const, Place.Carry, Place.Imm, value == 0 ? "clc" : "sec"));
                return;
            }

            var vreg = ((VirtualReg)carryIn).Id;
            if (_carryHolds == vreg) return;

            // A stored carry: bit 0 of its home goes into C.
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Reload, Place.A, Place.Home), Home(vreg));
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Lshr, Place.A, Place.None, "lsr"));
        }

        // The carry an operation produced in C: left there when the next operation
        // consumes it, else stored as 0 / 1. A is the operation's result (already
        // stored) and is free to use.
        internal void HandleCarryOut(MirOperand carryOut, List<MirInstruction> instrs, int index)
        {
            var vreg = ((VirtualReg)carryOut).Id;
            var uses = _uses.GetValueOrDefault(vreg);
            if (uses == 0) return;

            if (uses == 1 && ConsumedByNext(instrs, index, vreg))
            {
                _carryHolds = vreg;
                return;
            }

            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Const, Place.A, Place.Imm, "imm"), new Immediate(0));
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Shl, Place.A, Place.None, "rol"));
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Spill, Place.A, Place.Home), Home(vreg));
        }

        // Whether the next operation after instrs[index] (constants take no code)
        // reads `vreg` as its carry in.
        internal static bool ConsumedByNext(List<MirInstruction> instrs, int index, int vreg)
        {
            for (var j = index + 1; j < instrs.Count; j++)
            {
                var next = instrs[j];
                if (IsOp(next, MOS6502Op.Const)) continue;
                if (next.Opcode.Dialect != MOS6502Dialect.Id) return false;
                var carryIn = (MOS6502Op)next.Opcode.Code switch
                {
                    MOS6502Op.Adc or MOS6502Op.Sbc => 4,
                    MOS6502Op.Shle or MOS6502Op.Lshre => 3,
                    _ => -1,
                };
                return carryIn >= 0 && carryIn < next.Operands.Length
                    && next.Operands[carryIn] is VirtualReg { IsDefinition: false } use && use.Id == vreg;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        // %v = const <imm>: nothing to emit, the value is an immediate wherever it
        // is read. An address byte is not an immediate the ALU ops take, so it is
        // materialised into its home.
        internal void PlaceConst(MirInstruction instr)
        {
            if (instr.Operands[1] is Immediate) return;

            var symbol = (Symbol)instr.Operands[1];
            var half = ((Immediate)instr.Operands[2]).Value;
            EmitVariant(
                MOS6502Variants.Require(MOS6502Operation.Const, Place.A, Place.Abs, half == 0 ? "symlo" : "symhi"),
                symbol);
            StoreResult(instr.Operands[0], A);
        }

        // %r, %cout = adc|sbc %a, %b, %cin
        internal void PlaceAddSub(MirInstruction instr, MOS6502Operation operation, List<MirInstruction> instrs, int index)
        {
            EnsureCarry(instr.Operands[4]);
            LoadRegister(A, instr.Operands[2]);
            var (place, memory) = MemorySide(instr.Operands[3]);
            EmitVariant(MOS6502Variants.Require(operation, Place.A, place), memory);
            StoreResult(instr.Operands[0], A);
            HandleCarryOut(instr.Operands[1], instrs, index);
        }

        // %r = inc|dec %a: CLC; ADC #1 or SEC; SBC #1 (the naive placement works in A).
        internal void PlaceStep(MirInstruction instr, MOS6502Operation operation, string carry)
        {
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Const, Place.Carry, Place.Imm, carry));
            LoadRegister(A, instr.Operands[1]);
            EmitVariant(MOS6502Variants.Require(operation, Place.A, Place.Imm), new Immediate(1));
            StoreResult(instr.Operands[0], A);
        }

        // %r = and|ora|eor %a, %b
        internal void PlaceLogic(MirInstruction instr, MOS6502Operation operation)
        {
            LoadRegister(A, instr.Operands[1]);
            var (place, memory) = MemorySide(instr.Operands[2]);
            EmitVariant(MOS6502Variants.Require(operation, Place.A, place), memory);
            StoreResult(instr.Operands[0], A);
        }

        // %r, %cout = shle|lshre %a, %cin: asl / lsr for a zero carry in, else the
        // rotate through the carry.
        internal void PlaceShift(MirInstruction instr, MOS6502Operation operation, List<MirInstruction> instrs, int index)
        {
            var carryIn = instr.Operands[3];
            var plain = TryConst(carryIn, out var value) && value == 0;
            if (!plain) EnsureCarry(carryIn);
            LoadRegister(A, instr.Operands[2]);

            var name = operation == MOS6502Operation.Shl ? (plain ? "asl" : "rol") : (plain ? "lsr" : "ror");
            EmitVariant(MOS6502Variants.Require(operation, Place.A, Place.None, name));
            StoreResult(instr.Operands[0], A);
            HandleCarryOut(instr.Operands[1], instrs, index);
        }

        // %c = cmp %a, %b: the carry of the compare.
        internal void PlaceCmp(MirInstruction instr, List<MirInstruction> instrs, int index)
        {
            LoadRegister(A, instr.Operands[1]);
            var (place, memory) = MemorySide(instr.Operands[2]);
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Cmp, Place.A, place), memory);
            HandleCarryOut(instr.Operands[0], instrs, index);
        }

        // cmp_br %a, %b, cc, T [, cc, T2]: one compare, then a branch per exit.
        internal void PlaceCmpBr(MirInstruction instr)
        {
            LoadRegister(A, instr.Operands[0]);
            var (place, memory) = MemorySide(instr.Operands[1]);
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Cmp, Place.A, place), memory);
            EmitBranches(instr);
        }

        // The branches of a cmp_br on the flags its compare left: one per exit.
        internal void EmitBranches(MirInstruction instr)
        {
            for (var k = 2; k < instr.Operands.Length; k += 2)
            {
                var condition = (CmpBrCondition)((Immediate)instr.Operands[k]).Value;
                var target = EdgeTarget((BlockTarget)instr.Operands[k + 1]);
                var (branch, flag) = condition switch
                {
                    CmpBrCondition.Eq => (MOS6502Op.Beq, MOS6502Registers.Z),
                    CmpBrCondition.Ne => (MOS6502Op.Bne, MOS6502Registers.Z),
                    CmpBrCondition.Cs => (MOS6502Op.Bcs, C),
                    CmpBrCondition.Cc => (MOS6502Op.Bcc, C),
                    CmpBrCondition.Mi => (MOS6502Op.Bmi, MOS6502Registers.N),
                    CmpBrCondition.Pl => (MOS6502Op.Bpl, MOS6502Registers.N),
                    _ => throw new InvalidOperationException($"cmp_br: unknown condition {condition}."),
                };
                Emit(branch, target, new PhysicalReg(flag, IsDefinition: false, IsImplicit: true));
            }
        }

        // ------------------------------------------------------------------
        // Memory
        // ------------------------------------------------------------------

        internal bool IsFrameSlot(string symbol) => function.FrameSlots.Any(s => s.SymbolName == symbol);

        // %v = load @sym+off | %v = load %lo, %hi, off
        internal void PlaceLoad(MirInstruction instr)
        {
            var def = instr.Operands[0];
            if (instr.Operands[1] is Symbol symbol)
            {
                if (IsFrameSlot(symbol.Name))
                    Emit(MOS6502Op.FrameLoadByte,
                        new PhysicalReg(A, IsDefinition: true),
                        new Symbol(symbol.Name),
                        new Immediate(symbol.Offset),
                        new PhysicalReg(Y, IsDefinition: true, IsImplicit: true),
                        new PhysicalReg(MOS6502Variants.PointerLo, IsDefinition: true, IsImplicit: true),
                        new PhysicalReg(MOS6502Variants.PointerHi, IsDefinition: true, IsImplicit: true));
                else
                    EmitVariant(MOS6502Variants.Require(MOS6502Operation.Load, Place.A, Place.Abs), symbol);
            }
            else
            {
                SetUpPointer(instr.Operands[1], instr.Operands[2], ((Immediate)instr.Operands[3]).Value);
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Load, Place.A, Place.Pair));
            }
            StoreResult(def, A);
        }

        // store %v, @sym+off | store %v, %lo, %hi, off
        internal void PlaceStore(MirInstruction instr)
        {
            var value = instr.Operands[0];
            if (instr.Operands[1] is Symbol symbol)
            {
                LoadRegister(A, value);
                if (IsFrameSlot(symbol.Name))
                    Emit(MOS6502Op.FrameStoreByte,
                        new Symbol(symbol.Name),
                        new Immediate(symbol.Offset),
                        new PhysicalReg(A, IsDefinition: false),
                        new PhysicalReg(X, IsDefinition: true, IsImplicit: true),
                        new PhysicalReg(Y, IsDefinition: true, IsImplicit: true),
                        new PhysicalReg(MOS6502Variants.PointerLo, IsDefinition: true, IsImplicit: true),
                        new PhysicalReg(MOS6502Variants.PointerHi, IsDefinition: true, IsImplicit: true));
                else
                    EmitVariant(MOS6502Variants.Require(MOS6502Operation.Store, Place.A, Place.Abs), symbol);
            }
            else
            {
                SetUpPointer(instr.Operands[1], instr.Operands[2], ((Immediate)instr.Operands[3]).Value);
                LoadRegister(A, value);
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Store, Place.A, Place.Pair));
            }
        }

        // The two bytes of a pointer into the scratch pair, and the offset into Y.
        internal void SetUpPointer(MirOperand lo, MirOperand hi, long offset)
        {
            var spill = MOS6502Variants.Require(MOS6502Operation.Spill, Place.A, Place.Home);
            LoadRegister(A, lo);
            EmitVariant(spill, new PhysicalReg(MOS6502Variants.PointerLo, IsDefinition: false));
            LoadRegister(A, hi);
            EmitVariant(spill, new PhysicalReg(MOS6502Variants.PointerHi, IsDefinition: false));
            EmitVariant(MOS6502Variants.Require(MOS6502Operation.Const, Place.Y, Place.Imm, "imm"), new Immediate(offset));
        }

        // The frame access ops the call lowering emitted for stack arguments: the
        // byte moves through A.
        internal void PlaceFrameLoad(MirInstruction instr)
        {
            var operands = (MirOperand[])instr.Operands.Clone();
            var def = operands[0];
            operands[0] = new PhysicalReg(A, IsDefinition: true);
            Emit(MOS6502Op.FrameLoadByte, operands);
            StoreResult(def, A);
        }

        internal void PlaceFrameStore(MirInstruction instr)
        {
            var operands = (MirOperand[])instr.Operands.Clone();
            LoadRegister(A, operands[2]);
            operands[2] = new PhysicalReg(A, IsDefinition: false);
            Emit(MOS6502Op.FrameStoreByte, operands);
        }

        // ------------------------------------------------------------------
        // Copies
        // ------------------------------------------------------------------

        // A run of consecutive pseudo.copy ops. Copies to / from the fixed
        // registers are the call and entry/return conventions; their order matters
        // because loading a zero-page register goes through A:
        //   1. bytes read out of A, X, Y into homes (before anything clobbers them),
        //   2. bytes read out of zero-page registers into homes (through A),
        //   3. home to home,
        //   4. homes into zero-page registers (through A), then Y, X, and A last.
        internal void PlaceCopies(List<MirInstruction> run)
        {
            var fromGpr = new List<MirInstruction>();
            var fromZp = new List<MirInstruction>();
            var toHome = new List<MirInstruction>();
            var toZp = new List<MirInstruction>();
            var toX = new List<MirInstruction>();
            var toY = new List<MirInstruction>();
            var toA = new List<MirInstruction>();

            foreach (var copy in run)
            {
                var (dst, src) = (copy.Operands[0], copy.Operands[1]);
                if (dst is PhysicalReg d && src is PhysicalReg s)
                {
                    if (d.Id == s.Id) continue;
                    throw new NotSupportedException(
                        $"MOS6502NaivePlacement: a copy from ${MOS6502Registers.NameOf(s.Id)} to ${MOS6502Registers.NameOf(d.Id)}.");
                }

                if (dst is PhysicalReg to)
                {
                    if (to.Id is not (A or X or Y) && to.Id < MOS6502Registers.RC(0))
                        throw new NotSupportedException(
                            $"MOS6502NaivePlacement: a copy into ${MOS6502Registers.NameOf(to.Id)}.");
                    (to.Id switch { A => toA, X => toX, Y => toY, _ => toZp }).Add(copy);
                }
                else if (src is PhysicalReg from)
                {
                    (from.Id is A or X or Y ? fromGpr : fromZp).Add(copy);
                }
                else
                {
                    toHome.Add(copy);
                }
            }

            foreach (var copy in fromGpr)
            {
                var reg = ((PhysicalReg)copy.Operands[1]).Id;
                StoreResult(copy.Operands[0], reg);
            }
            foreach (var copy in fromZp)
            {
                if (Unused(copy.Operands[0])) continue;
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Reload, Place.A, Place.Home),
                    new PhysicalReg(((PhysicalReg)copy.Operands[1]).Id, IsDefinition: false));
                StoreResult(copy.Operands[0], A);
            }
            foreach (var copy in toHome)
            {
                if (Unused(copy.Operands[0])) continue;
                LoadRegister(A, copy.Operands[1]);
                StoreResult(copy.Operands[0], A);
            }
            foreach (var copy in toZp)
            {
                LoadRegister(A, copy.Operands[1]);
                EmitVariant(MOS6502Variants.Require(MOS6502Operation.Spill, Place.A, Place.Home),
                    new PhysicalReg(((PhysicalReg)copy.Operands[0]).Id, IsDefinition: false));
            }
            foreach (var copy in toY) LoadRegister(Y, copy.Operands[1]);
            foreach (var copy in toX) LoadRegister(X, copy.Operands[1]);
            foreach (var copy in toA) LoadRegister(A, copy.Operands[1]);
        }

        // ------------------------------------------------------------------
        // Edges
        // ------------------------------------------------------------------

        // The target a conditional exit branches to: itself when it needs no moves,
        // else a new block that moves the arguments into the parameters and jumps.
        internal BlockTarget EdgeTarget(BlockTarget target)
        {
            if (target.Args.Length == 0 || EdgeMoves(target).Count == 0)
                return new BlockTarget(target.Block, []);

            var (savedOut, savedCarry) = (_out, _carryHolds);
            _out = [];
            _carryHolds = -1;
            EmitEdgeMoves(target);
            Emit(MOS6502Op.JmpAbs, new BlockTarget(target.Block, []));

            var edge = function.CreateBlock();
            _edgeBlocks.Add(edge);
            foreach (var instr in _out)
            {
                instr.Parent = edge;
                edge.Instructions.Add(instr);
            }
            (_out, _carryHolds) = (savedOut, savedCarry);
            return new BlockTarget(edge, []);
        }

        // The moves a branch makes: each used parameter that does not already share
        // its home with the argument it receives.
        internal List<(int Parameter, MirOperand Source)> EdgeMoves(BlockTarget target)
        {
            var parameters = target.Block.Parameters;
            if (parameters.Count != target.Args.Length)
                throw new InvalidOperationException(
                    $"MOS6502NaivePlacement: a branch passes {target.Args.Length} arguments to a block with {parameters.Count} parameters.");

            var moves = new List<(int Parameter, MirOperand Source)>();
            for (var k = 0; k < parameters.Count; k++)
            {
                var source = target.Args[k];
                if (source is VirtualReg v && !_constants.ContainsKey(v.Id) && HomeKey(v.Id) == HomeKey(parameters[k])) continue;
                if (_uses.GetValueOrDefault(parameters[k]) == 0) continue;
                moves.Add((parameters[k], source));
            }
            return moves;
        }

        // Whether a move reads a home that another move of the same branch writes,
        // so the moves cannot simply run one after the other.
        internal bool HasMoveHazard(List<(int Parameter, MirOperand Source)> moves)
        {
            var written = moves.Select(m => HomeKey(m.Parameter)).ToHashSet();
            return moves.Any(m => m.Source is VirtualReg { IsDefinition: false } v
                && !_constants.ContainsKey(v.Id) && written.Contains(HomeKey(v.Id)));
        }

        // The parallel move of a branch's arguments into the target's parameters.
        internal void EmitEdgeMoves(BlockTarget target)
        {
            var moves = EdgeMoves(target);
            var hazard = HasMoveHazard(moves);

            var spill = MOS6502Variants.Require(MOS6502Operation.Spill, Place.A, Place.Home);
            if (!hazard)
            {
                foreach (var (parameter, source) in moves)
                {
                    LoadRegister(A, source);
                    EmitVariant(spill, Home(parameter));
                }
                return;
            }

            var reload = MOS6502Variants.Require(MOS6502Operation.Reload, Place.A, Place.Home);
            var temporaries = moves.Select(_ => NewTemporaryHome()).ToList();
            for (var k = 0; k < moves.Count; k++)
            {
                LoadRegister(A, moves[k].Source);
                EmitVariant(spill, temporaries[k]);
            }
            for (var k = 0; k < moves.Count; k++)
            {
                EmitVariant(reload, temporaries[k]);
                EmitVariant(spill, Home(moves[k].Parameter));
            }
        }

        // ------------------------------------------------------------------
        // Block live-ins
        // ------------------------------------------------------------------

        // Live-in registers of every block, from the physical registers the code
        // now reads and writes: A, X, Y, the zero-page registers and the homes (the
        // flags and the fixed registers are not tracked). The entry block keeps the
        // calling convention's declared live-ins.
        internal void RecomputeLiveIns()
        {
            static bool Tracked(int reg) =>
                reg is A or X or Y || reg >= MOS6502Registers.RC(2);

            var liveIn = function.Blocks.ToDictionary(b => b, _ => new HashSet<int>());
            bool changed;
            do
            {
                changed = false;
                for (var bi = function.Blocks.Count - 1; bi >= 0; bi--)
                {
                    var block = function.Blocks[bi];
                    var live = new HashSet<int>();
                    foreach (var successor in block.Successors)
                        live.UnionWith(liveIn[successor]);

                    for (var ii = block.Instructions.Count - 1; ii >= 0; ii--)
                    {
                        var instr = block.Instructions[ii];
                        foreach (var operand in instr.Operands)
                            if (operand is PhysicalReg { IsDefinition: true } def)
                                live.Remove(def.Id);
                        foreach (var operand in instr.Operands)
                            if (operand is PhysicalReg { IsDefinition: false } use && Tracked(use.Id))
                                live.Add(use.Id);
                    }

                    if (!live.SetEquals(liveIn[block]))
                    {
                        liveIn[block] = live;
                        changed = true;
                    }
                }
            } while (changed);

            for (var bi = 0; bi < function.Blocks.Count; bi++)
            {
                var block = function.Blocks[bi];
                var declared = bi == 0 ? block.LiveIns.ToList() : [];
                block.LiveIns.Clear();
                foreach (var reg in liveIn[block].Union(declared).OrderBy(r => r))
                    block.LiveIns.Add(reg);
            }
        }
    }
}
