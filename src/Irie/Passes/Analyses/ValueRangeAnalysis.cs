using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
using Irie.Dialects.Cf;
using Irie.Mir;

namespace Irie.Passes.Analyses;

/// <summary>
/// Forward value-range analysis on generic SSA MIR: for each integer vreg, a closed interval
/// (<see cref="ValueRange"/>) that bounds every value it can hold.
/// </summary>
/// <remarks>
/// Seeds are constants, the extensions (<c>zext</c> of iM is within [0, 2^M-1]) and ranges the MIR text
/// already declares (<c>%x : i32 in [0, 9]</c>, taken as true). Transfer functions cover add, sub, mul,
/// shifts by a constant, the bitwise ops, unsigned divide and remainder, select and truncation. Each computes
/// the mathematical result interval in <see cref="Int128"/> and then wraps it to the vreg's width: an interval
/// that fits keeps its bounds, one that straddles the width's edge becomes the full range. Anything else
/// (loads, calls, signed division) is unknown, which is the full range of the type.
///
/// A block parameter is the join of the ranges its incoming edges pass. On a conditional branch whose condition
/// is a <c>cmpi</c>, an edge argument that is one of the compared values is first narrowed by what the
/// predicate implies on that edge (for <c>i &lt; 5</c> the body edge passes an <c>i</c> of at most 4); the
/// narrowed range belongs to the edge argument, not to the compared vreg.
///
/// Loops are solved by iterating the blocks in reverse post-order to a fixpoint. A value not yet computed (the
/// argument of a back edge in the first round) contributes nothing. A bound of a block parameter that is still
/// moving after two rounds jumps to the type's limit (widening), so a counter that grows by one each trip
/// settles at once instead of after 2^32 rounds; narrowing rounds then recompute every value from the
/// widened ones, and the branch refinement pulls the bounds back (<c>i &lt; len</c> bounds the counter by
/// the range of <c>len</c>).
/// </remarks>
public static class ValueRangeAnalysis
{
    /// <summary>Rounds before a still-moving bound of a block parameter is widened to the type's limit.</summary>
    private const int WideningDelay = 2;

    /// <summary>Rounds of recomputation after the fixpoint that pull widened bounds back.</summary>
    private const int NarrowingRounds = 2;

    /// <summary>Gives up (no ranges) if the ascending iteration has not settled after this many rounds.</summary>
    private const int MaxRounds = 40;

    /// <summary>
    /// The known ranges, by vreg id. A vreg whose range is the full range of its type has no entry.
    /// </summary>
    public static Dictionary<int, ValueRange> Compute(MirFunction function)
    {
        if (function.Blocks.Count == 0)
            return [];

        function.RebuildCfg();
        var solver = new Solver(function);
        var ranges = solver.Solve();
        foreach (var vreg in ranges.Keys.ToArray())
            if (IntegerBits(function, vreg) is not { } bits || ranges[vreg].IsFull(bits))
                ranges.Remove(vreg);
        return ranges;
    }

    private sealed class Solver
    {
        private readonly MirFunction _function;
        private readonly List<MirBlock> _order;
        private readonly Dictionary<int, MirInstruction> _compares = [];

        /// <summary>The range of each integer vreg computed so far; a missing entry is "not computed yet".</summary>
        private readonly Dictionary<int, ValueRange> _ranges = [];

        private readonly HashSet<int> _blockParameters = [];

        /// <summary>Block parameters and arithmetic results: values a loop can feed back into itself.</summary>
        private readonly HashSet<int> _loopCarriable = [];

        private bool _changed;

        /// <summary>The bounds that widening may jump to before the type's limit, ascending.</summary>
        private List<Int128> _thresholds = [];

        public Solver(MirFunction function)
        {
            _function = function;
            foreach (var block in function.Blocks)
            {
                _blockParameters.UnionWith(block.Parameters);
                _loopCarriable.UnionWith(block.Parameters);
                foreach (var instruction in block.Instructions)
                    if (instruction.Opcode.Dialect == ArithDialect.Id
                        && instruction.Opcode != ArithDialect.OpRef(ArithOp.Constant)
                        && instruction.Operands.Length > 0 && instruction.Operands[0] is VirtualReg def)
                        _loopCarriable.Add(def.Id);
            }
            _order = ReversePostOrder(function);
            foreach (var block in _order)
                foreach (var instruction in block.Instructions)
                    if (instruction.Opcode == ArithDialect.OpRef(ArithOp.CmpI)
                        && instruction.Operands[0] is VirtualReg def)
                        _compares[def.Id] = instruction;
        }

        public Dictionary<int, ValueRange> Solve()
        {
            foreach (var parameter in _function.Blocks[0].Parameters)
                if (IntegerBits(_function, parameter) is { } bits)
                    Set(parameter, ValueRange.Full(bits), widen: false, narrowing: false);

            var round = 0;
            do
            {
                _changed = false;
                Sweep(widen: round >= WideningDelay, narrowing: false);
                if (++round > MaxRounds)
                    return [];
            } while (_changed);

            for (var i = 0; i < NarrowingRounds; i++)
                Sweep(widen: false, narrowing: true);

            return _ranges;
        }

        private void Sweep(bool widen, bool narrowing)
        {
            if (widen)
                _thresholds = CompareBounds();
            foreach (var block in _order)
            {
                if (block != _function.Blocks[0])
                    ComputeBlockParameters(block, widen, narrowing);
                foreach (var instruction in block.Instructions)
                    ComputeInstruction(instruction, narrowing);
            }
        }

        private void ComputeBlockParameters(MirBlock block, bool widen, bool narrowing)
        {
            for (var i = 0; i < block.Parameters.Count; i++)
            {
                var parameter = block.Parameters[i];
                if (IntegerBits(_function, parameter) is not { } bits)
                    continue;

                ValueRange? joined = null;
                foreach (var predecessor in block.Predecessors)
                {
                    foreach (var instruction in predecessor.Instructions)
                    {
                        for (var k = 0; k < instruction.Operands.Length; k++)
                        {
                            if (instruction.Operands[k] is not BlockTarget target || target.Block != block)
                                continue;
                            if (i >= target.Args.Length || target.Args[i] is not VirtualReg arg)
                            {
                                joined = ValueRange.Full(bits);
                                continue;
                            }
                            if (EdgeRange(instruction, k, arg.Id) is { } incoming)
                                joined = joined?.Join(incoming) ?? incoming;
                        }
                    }
                }

                if (joined is not null)
                    Set(parameter, joined, widen, narrowing);
            }
        }

        private void ComputeInstruction(MirInstruction instruction, bool narrowing)
        {
            var defs = instruction.Operands
                .OfType<VirtualReg>().Where(o => o.IsDefinition).Select(o => o.Id).ToList();
            if (defs.Count == 0)
                return;

            // A use that has no range yet (the argument of a back edge, in the first round) leaves the result
            // not computed; the next round gets it.
            foreach (var operand in instruction.Operands)
                if (operand is VirtualReg { IsDefinition: false } use
                    && IntegerBits(_function, use.Id) is not null && !_ranges.ContainsKey(use.Id))
                    return;

            // Instructions with several defs (carry chains, multi-result calls) are post-legalization or opaque.
            var range = defs.Count == 1 && IntegerBits(_function, defs[0]) is { } defBits
                ? Transfer(instruction, defBits, _ranges, _function)
                : null;
            foreach (var def in defs)
            {
                if (IntegerBits(_function, def) is not { } bits)
                    continue;
                Set(def, defs.Count == 1 ? range ?? ValueRange.Full(bits) : ValueRange.Full(bits), widen: false, narrowing);
            }
        }

        /// <summary>
        /// Stores the range of a vreg, intersected with the one its annotation already declares. While ascending
        /// the stored range only grows (and, once <paramref name="widen"/>, a block parameter's moving bound
        /// jumps to the type's limit); while narrowing the new range replaces it if it is contained in it.
        /// </summary>
        private void Set(int vreg, ValueRange computed, bool widen, bool narrowing)
        {
            var bits = IntegerBits(_function, vreg)!.Value;
            var range = computed;
            if (_function.GetVRegAnnotation(vreg) is TypedVReg { Range: { } declared })
                range = range.Intersect(declared) ?? declared;

            if (_ranges.TryGetValue(vreg, out var old))
            {
                if (narrowing)
                {
                    // Only ever a contained range: anything else would not be a post-fixpoint.
                    if (range.Intersect(old) != range)
                        return;
                }
                else
                {
                    var joined = old.Join(range);
                    if (widen && _blockParameters.Contains(vreg) && joined != old)
                    {
                        var full = ValueRange.Full(bits);
                        joined = new ValueRange(
                            joined.Lo < old.Lo ? _thresholds.LastOrDefault(t => t <= joined.Lo, full.Lo) : joined.Lo,
                            joined.Hi > old.Hi ? _thresholds.FirstOrDefault(t => t >= joined.Hi, full.Hi) : joined.Hi);
                    }
                    range = joined;
                }
            }

            if (old == range)
                return;
            _ranges[vreg] = range;
            _changed = true;
        }

        /// <summary>
        /// The bounds of the compared values that no loop feeds back into themselves, ascending. A counter that is still growing is widened to the
        /// nearest one rather than straight to the type's limit: adding one to a counter at the limit wraps and
        /// would hide the bound the loop test gives it.
        /// </summary>
        private List<Int128> CompareBounds()
        {
            var bounds = new SortedSet<Int128>();
            foreach (var compare in _compares.Values)
            {
                foreach (var operand in compare.Operands.OfType<VirtualReg>())
                {
                    if (!operand.IsDefinition && !_loopCarriable.Contains(operand.Id)
                        && _ranges.TryGetValue(operand.Id, out var range))
                    {
                        bounds.Add(range.Lo);
                        bounds.Add(range.Hi);
                    }
                }
            }
            return [.. bounds];
        }

        /// <summary>
        /// The range of the argument that edge <paramref name="operandIndex"/> of <paramref name="branch"/> passes
        /// for <paramref name="argument"/>, narrowed by the branch condition; null while the argument has no range.
        /// </summary>
        private ValueRange? EdgeRange(MirInstruction branch, int operandIndex, int argument)
        {
            if (!_ranges.TryGetValue(argument, out var range))
                return null;

            if (branch.Opcode != CfDialect.OpRef(CfOp.CondBr)
                || branch.Operands[0] is not VirtualReg condition
                || !_compares.TryGetValue(condition.Id, out var compare)
                || compare.Operands is not [_, Immediate predicateOperand, VirtualReg x, VirtualReg y])
                return range;

            var predicate = (ArithCmpPredicate)predicateOperand.Value;
            // Operand 1 is the edge taken when the condition holds, operand 2 when it does not.
            if (operandIndex == 2)
                predicate = ArithCmpPredicateOps.Inverse(predicate);

            ValueRange? other;
            if (argument == x.Id)
                other = _ranges.GetValueOrDefault(y.Id);
            else if (argument == y.Id)
            {
                other = _ranges.GetValueOrDefault(x.Id);
                predicate = ArithCmpPredicateOps.Swapped(predicate);
            }
            else
                return range;

            return other is null ? range : Refine(range, predicate, other);
        }

        /// <summary>The part of <paramref name="range"/> where <c>value predicate other</c> can hold.</summary>
        private static ValueRange Refine(ValueRange range, ArithCmpPredicate predicate, ValueRange other)
        {
            var bothNonNegative = range.Lo >= 0 && other.Lo >= 0;
            ValueRange? bound = predicate switch
            {
                ArithCmpPredicate.Eq => other,
                ArithCmpPredicate.Ne when other.Lo == other.Hi && range.Lo == other.Lo => new ValueRange(range.Lo + 1, range.Hi),
                ArithCmpPredicate.Ne when other.Lo == other.Hi && range.Hi == other.Lo => new ValueRange(range.Lo, range.Hi - 1),
                ArithCmpPredicate.Slt => new ValueRange(range.Lo, other.Hi - 1),
                ArithCmpPredicate.Sle => new ValueRange(range.Lo, other.Hi),
                ArithCmpPredicate.Sgt => new ValueRange(other.Lo + 1, range.Hi),
                ArithCmpPredicate.Sge => new ValueRange(other.Lo, range.Hi),
                // An unsigned value below a non-negative bound is itself non-negative and below it.
                ArithCmpPredicate.Ult when other.Lo >= 0 => new ValueRange(0, other.Hi - 1),
                ArithCmpPredicate.Ule when other.Lo >= 0 => new ValueRange(0, other.Hi),
                ArithCmpPredicate.Ugt when bothNonNegative => new ValueRange(other.Lo + 1, range.Hi),
                ArithCmpPredicate.Uge when bothNonNegative => new ValueRange(other.Lo, range.Hi),
                _ => null,
            };

            // An empty overlap means the edge is never taken; any range is sound there, so keep the old one.
            return bound is { Lo: var lo, Hi: var hi } && lo <= hi ? range.Intersect(bound) ?? range : range;
        }
    }

    private static List<MirBlock> ReversePostOrder(MirFunction function)
    {
        var visited = new HashSet<MirBlock>();
        var postOrder = new List<MirBlock>(function.Blocks.Count);

        void Dfs(MirBlock block)
        {
            if (!visited.Add(block))
                return;
            foreach (var successor in block.Successors)
                Dfs(successor);
            postOrder.Add(block);
        }

        Dfs(function.Blocks[0]);
        postOrder.Reverse();
        return postOrder;
    }

    private static int? IntegerBits(MirFunction function, int vreg) =>
        function.GetVRegAnnotation(vreg) is TypedVReg { Type: IntegerType type } ? type.SizeInBits : null;

    private static ValueRange? Transfer(
        MirInstruction instruction, int bits, Dictionary<int, ValueRange> ranges, MirFunction function)
    {
        var operands = instruction.Operands;
        var dialect = instruction.Opcode.Dialect;
        var code = instruction.Opcode.Code;

        ValueRange? Use(int index) =>
            operands[index] is VirtualReg { IsDefinition: false } use ? ranges.GetValueOrDefault(use.Id) : null;

        int SourceBits(int index) =>
            operands[index] is VirtualReg use ? IntegerBits(function, use.Id) ?? bits : bits;

        if (dialect == CastDialect.Id && operands.Length == 2)
        {
            if (Use(1) is not { } source)
                return null;
            var sourceBits = SourceBits(1);
            switch ((CastOp)code)
            {
                case CastOp.Zext:
                    return ToUnsigned(source, sourceBits);
                case CastOp.Sext:
                    // An i1 reads unsigned, so its sign extension negates it: 1 becomes -1.
                    return sourceBits == 1 ? new ValueRange(-source.Hi, -source.Lo) : source;
                case CastOp.Trunc:
                    return Wrap(source.Lo, source.Hi, bits);
            }
            return null;
        }

        if (dialect != ArithDialect.Id)
            return null;

        var op = (ArithOp)code;
        switch (op)
        {
            case ArithOp.Constant when operands is [_, Immediate imm]:
                var value = SignedReading(imm.Value, bits);
                return new ValueRange(value, value);

            case ArithOp.Select when operands.Length == 4:
                return Use(2) is { } t && Use(3) is { } f ? t.Join(f) : null;
        }

        if (operands.Length != 3)
            return null;
        if (Use(1) is not { } a || Use(2) is not { } b)
            return null;

        switch (op)
        {
            case ArithOp.AddI:
                return Wrap(a.Lo + b.Lo, a.Hi + b.Hi, bits);
            case ArithOp.SubI:
                return Wrap(a.Lo - b.Hi, a.Hi - b.Lo, bits);
            case ArithOp.MulI:
            {
                Int128[] products = [a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi];
                return Wrap(products.Min(), products.Max(), bits);
            }

            case ArithOp.Shl when b.Lo == b.Hi && ValidShift(b.Lo, bits):
                return Wrap(a.Lo << (int)b.Lo, a.Hi << (int)b.Lo, bits);
            case ArithOp.LShr when b.Lo == b.Hi && ValidShift(b.Lo, bits):
            {
                var unsigned = ToUnsigned(a, bits);
                return Wrap(unsigned.Lo >> (int)b.Lo, unsigned.Hi >> (int)b.Lo, bits);
            }
            case ArithOp.AShr when b.Lo == b.Hi && ValidShift(b.Lo, bits):
                return new ValueRange(a.Lo >> (int)b.Lo, a.Hi >> (int)b.Lo);

            case ArithOp.And:
                // Either operand being non-negative clears the sign bit and bounds the result by itself.
                if (a.Lo >= 0 && b.Lo >= 0)
                    return new ValueRange(0, Int128.Min(a.Hi, b.Hi));
                if (a.Lo >= 0)
                    return new ValueRange(0, a.Hi);
                if (b.Lo >= 0)
                    return new ValueRange(0, b.Hi);
                return null;
            case ArithOp.Or when a.Lo >= 0 && b.Lo >= 0:
                return new ValueRange(Int128.Max(a.Lo, b.Lo), AllOnesBelow(Int128.Max(a.Hi, b.Hi)));
            case ArithOp.Xor when a.Lo >= 0 && b.Lo >= 0:
                return new ValueRange(0, AllOnesBelow(Int128.Max(a.Hi, b.Hi)));

            case ArithOp.UDivI:
            {
                var dividend = ToUnsigned(a, bits);
                var divisor = ToUnsigned(b, bits);
                // Division by zero is undefined, so the divisor is at least 1.
                var smallest = Int128.Max(divisor.Lo, 1);
                var largest = Int128.Max(divisor.Hi, 1);
                return new ValueRange(dividend.Lo / largest, dividend.Hi / smallest);
            }
            case ArithOp.URemI:
            {
                var dividend = ToUnsigned(a, bits);
                var divisor = ToUnsigned(b, bits);
                return new ValueRange(0, Int128.Min(dividend.Hi, Int128.Max(divisor.Hi, 1) - 1));
            }
        }

        return null;
    }

    private static bool ValidShift(Int128 amount, int bits) => amount >= 0 && amount < bits;

    /// <summary>The value of an immediate read as a signed integer of the given width (i1 reads unsigned).</summary>
    private static Int128 SignedReading(long value, int bits)
    {
        if (bits == 1)
            return value & 1;
        if (bits >= 64)
            return value;
        var masked = value & ((1L << bits) - 1);
        return (masked >> (bits - 1)) != 0 ? masked - (1L << bits) : masked;
    }

    /// <summary>
    /// The range of the same bit patterns read as unsigned: a range of negative values moves up by 2^bits, one
    /// that straddles zero covers every unsigned value.
    /// </summary>
    private static ValueRange ToUnsigned(ValueRange range, int bits)
    {
        if (range.Lo >= 0)
            return range;
        var modulus = Int128.One << bits;
        return range.Hi < 0
            ? new ValueRange(range.Lo + modulus, range.Hi + modulus)
            : new ValueRange(0, modulus - 1);
    }

    /// <summary>2^k - 1 for the smallest k with 2^k greater than the value: all the bits an or/xor can set.</summary>
    private static Int128 AllOnesBelow(Int128 value)
    {
        Int128 mask = 0;
        while (mask < value)
            mask = (mask << 1) | 1;
        return mask;
    }

    /// <summary>
    /// Reduces the mathematical interval [lo, hi] to a width: the same bounds when every value in it reads the
    /// same way modulo 2^bits, the full range when the interval wraps around.
    /// </summary>
    private static ValueRange Wrap(Int128 lo, Int128 hi, int bits)
    {
        var modulus = Int128.One << bits;
        if (hi - lo >= modulus)
            return ValueRange.Full(bits);

        var low = SignedReading((long)(ulong)(lo & (modulus - 1)), bits);
        var high = SignedReading((long)(ulong)(hi & (modulus - 1)), bits);
        return low <= high && high - low == hi - lo ? new ValueRange(low, high) : ValueRange.Full(bits);
    }
}
