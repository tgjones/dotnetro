using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
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
/// (loads, calls, signed division) is unknown and has no entry in the result.
///
/// Blocks are visited in reverse post-order in a single pass. A block parameter is the join of the ranges its
/// incoming edges pass; a parameter with an incoming edge not yet visited (a loop back edge) is unknown.
/// </remarks>
public static class ValueRangeAnalysis
{
    /// <summary>
    /// The known ranges, by vreg id. A vreg whose range is the full range of its type has no entry.
    /// </summary>
    public static Dictionary<int, ValueRange> Compute(MirFunction function)
    {
        var ranges = new Dictionary<int, ValueRange>();
        if (function.Blocks.Count == 0)
            return ranges;

        function.RebuildCfg();
        var order = ReversePostOrder(function);
        var visited = new HashSet<MirBlock>();

        foreach (var block in order)
        {
            ComputeBlockParameters(function, block, visited, ranges);
            foreach (var instruction in block.Instructions)
                ComputeInstruction(function, instruction, ranges);
            visited.Add(block);
        }

        return ranges;
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

    private static void ComputeBlockParameters(
        MirFunction function, MirBlock block, HashSet<MirBlock> visited, Dictionary<int, ValueRange> ranges)
    {
        for (var i = 0; i < block.Parameters.Count; i++)
        {
            var parameter = block.Parameters[i];
            if (IntegerBits(function, parameter) is not { } bits)
                continue;

            ValueRange? joined = null;
            var known = block != function.Blocks[0] && block.Predecessors.All(visited.Contains);
            if (known)
            {
                foreach (var predecessor in block.Predecessors)
                {
                    foreach (var instruction in predecessor.Instructions)
                    {
                        foreach (var operand in instruction.Operands)
                        {
                            if (operand is not BlockTarget { } target || target.Block != block)
                                continue;
                            var incoming = i < target.Args.Length && target.Args[i] is VirtualReg arg
                                ? RangeOf(function, arg.Id, ranges)
                                : null;
                            joined = incoming is null ? ValueRange.Full(bits) : joined?.Join(incoming) ?? incoming;
                        }
                    }
                }
            }

            Record(function, parameter, bits, joined, ranges);
        }
    }

    private static void ComputeInstruction(
        MirFunction function, MirInstruction instruction, Dictionary<int, ValueRange> ranges)
    {
        if (instruction.Operands.Length == 0 || instruction.Operands[0] is not VirtualReg { IsDefinition: true } def)
            return;
        // Instructions with several defs (carry chains, multi-result calls) are post-legalization or opaque.
        if (instruction.Operands.Skip(1).Any(o => o is VirtualReg { IsDefinition: true }))
            return;
        if (IntegerBits(function, def.Id) is not { } bits)
            return;

        var range = Transfer(function, instruction, bits, ranges);
        Record(function, def.Id, bits, range, ranges);
    }

    /// <summary>
    /// Stores the range of a vreg, intersected with the one its annotation already declares; a full range is
    /// not stored.
    /// </summary>
    private static void Record(
        MirFunction function, int vreg, int bits, ValueRange? computed, Dictionary<int, ValueRange> ranges)
    {
        var range = computed;
        if (function.GetVRegAnnotation(vreg) is TypedVReg { Range: { } declared })
            range = range is null ? declared : range.Intersect(declared) ?? declared;
        if (range is null || range.IsFull(bits))
            ranges.Remove(vreg);
        else
            ranges[vreg] = range;
    }

    private static int? IntegerBits(MirFunction function, int vreg) =>
        function.GetVRegAnnotation(vreg) is TypedVReg { Type: IntegerType type } ? type.SizeInBits : null;

    /// <summary>The range of a vreg: what is known, else the full range of its type.</summary>
    private static ValueRange? RangeOf(MirFunction function, int vreg, Dictionary<int, ValueRange> ranges)
    {
        if (ranges.TryGetValue(vreg, out var range))
            return range;
        return IntegerBits(function, vreg) is { } bits ? ValueRange.Full(bits) : null;
    }

    private static ValueRange? Transfer(
        MirFunction function, MirInstruction instruction, int bits, Dictionary<int, ValueRange> ranges)
    {
        var operands = instruction.Operands;
        var dialect = instruction.Opcode.Dialect;
        var code = instruction.Opcode.Code;

        ValueRange? Use(int index) =>
            operands[index] is VirtualReg { IsDefinition: false } use ? RangeOf(function, use.Id, ranges) : null;

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
