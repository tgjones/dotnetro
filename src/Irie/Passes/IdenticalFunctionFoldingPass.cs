using Irie.Mir;

namespace Irie.Passes;

/// <summary>
/// Folds functions with identical bodies into one, on generic SSA MIR (identical code folding; LLVM's
/// <c>MergeFunctions</c>).
/// </summary>
/// <remarks>
/// Two functions are identical when their signatures, frame slots and blocks match instruction by
/// instruction, with virtual registers matching up one to one (so numbering is irrelevant), block targets
/// naming the same block positions, and symbols naming the same thing. A recursive call to the function itself
/// matches a recursive call to the other. Functions are first bucketed by a structural hash of exactly that
/// view, then compared in full inside a bucket, so a hash collision costs time but never merges different code.
///
/// A symbol that names a function is compared through its fold target, so the pass iterates: once two callees
/// fold, their callers' bodies become identical and fold in the next round. It stops when a round folds
/// nothing. A cycle of functions that only call each other (A calls B calls A, mirrored elsewhere) is not
/// recognised, as in LLVM.
///
/// Runs before inlining: folding raises the survivor's call count, so it stays one shared body instead of being
/// pasted into each caller (which the pass could no longer fold). Every reference to a dropped function (a call,
/// a <c>mem.symbol</c>, a vtable slot) is redirected to the survivor, which is the earliest of the group in the
/// module. The module is the whole program, so the dropped function has no other users. Functions the
/// inliner treats specially (<c>noinline</c>, the entry point, anything already lowered) are left alone.
/// </remarks>
public sealed class IdenticalFunctionFoldingPass : Pass
{
    private const string EntryFunction = "__entry";

    public override string Name => "IdenticalFunctionFolding";

    public override void Run(CompilationContext context)
    {
        var module = context.Module;
        var candidates = module.Functions.Where(IsCandidate).ToList();
        if (candidates.Count < 2)
            return;

        var foldedInto = new Dictionary<string, string>();
        var survivors = new List<MirFunction>(candidates);
        var folded = 0;

        bool changed;
        do
        {
            changed = false;
            var comparer = new FunctionComparer(foldedInto);

            var buckets = new Dictionary<int, List<MirFunction>>();
            foreach (var function in survivors)
            {
                var hash = comparer.Hash(function);
                if (!buckets.TryGetValue(hash, out var bucket))
                    buckets[hash] = bucket = [];

                var match = bucket.FirstOrDefault(other => comparer.Equivalent(other, function));
                if (match is null)
                {
                    bucket.Add(function);
                    continue;
                }

                foldedInto[function.Name] = match.Name;
                folded++;
                changed = true;
            }

            survivors.RemoveAll(function => foldedInto.ContainsKey(function.Name));
        } while (changed);

        context.Statistics["function-folding.functions-folded"] = folded;
        if (folded == 0)
            return;

        Redirect(module, foldedInto);
        module.Functions.RemoveAll(function => foldedInto.ContainsKey(function.Name));
    }

    private static bool IsCandidate(MirFunction function) =>
        function.Name != EntryFunction
        && !function.NoInline
        && function.Convention is null
        && function.Blocks.Count > 0
        && InliningPass.IsPlainGeneric(function);

    /// <summary>Point every reference to a folded function at its survivor.</summary>
    private static void Redirect(MirModule module, Dictionary<string, string> foldedInto)
    {
        string Resolve(string name)
        {
            while (foldedInto.TryGetValue(name, out var next))
                name = next;
            return name;
        }

        bool IsAffected(MirOperand operand) => operand is Symbol symbol && foldedInto.ContainsKey(symbol.Name)
            || operand is BlockTarget target && target.Args.Any(IsAffected);

        MirOperand Rewrite(MirOperand operand) => operand switch
        {
            Symbol symbol when foldedInto.ContainsKey(symbol.Name) => symbol with { Name = Resolve(symbol.Name) },
            BlockTarget target => target with { Args = [.. target.Args.Select(Rewrite)] },
            _ => operand,
        };

        foreach (var function in module.Functions)
            foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions)
                    if (instruction.Operands.Any(IsAffected))
                        instruction.Operands = [.. instruction.Operands.Select(Rewrite)];

        for (var i = 0; i < module.Globals.Count; i++)
        {
            var global = module.Globals[i];
            if (global.Initializer is not { } initializer
                || !initializer.Items.Any(item => item is DataSymbolRef reference && foldedInto.ContainsKey(reference.SymbolName)))
                continue;

            var items = initializer.Items
                .Select(item => item is DataSymbolRef reference && foldedInto.ContainsKey(reference.SymbolName)
                    ? new DataSymbolRef(Resolve(reference.SymbolName))
                    : item)
                .ToArray();
            module.Globals[i] = global with { Initializer = new MirInitializer(items) };
        }
    }

    /// <summary>
    /// The structural hash and the full comparison of two functions. Both view a symbol naming a folded function
    /// as its fold target, and a symbol naming the function itself as "self".
    /// </summary>
    private sealed class FunctionComparer(Dictionary<string, string> foldedInto)
    {
        private string Resolve(string name)
        {
            while (foldedInto.TryGetValue(name, out var next))
                name = next;
            return name;
        }

        /// <summary>
        /// Equal for equivalent functions: it mixes in only what <see cref="Equivalent"/> compares, with virtual
        /// registers numbered by first appearance.
        /// </summary>
        public int Hash(MirFunction function)
        {
            var hash = new HashCode();
            var vregs = new Dictionary<int, int>();
            var blockIndex = BlockIndexes(function);

            hash.Add(function.AlwaysInline);
            hash.Add(function.ReturnType);
            foreach (var type in function.ParameterTypes)
                hash.Add(type);
            foreach (var slot in function.FrameSlots)
            {
                hash.Add(slot.Index);
                hash.Add(slot.Type);
            }

            hash.Add(function.Blocks.Count);
            foreach (var block in function.Blocks)
            {
                hash.Add(block.Parameters.Count);
                foreach (var parameter in block.Parameters)
                    hash.Add(Number(vregs, parameter));

                hash.Add(block.Instructions.Count);
                foreach (var instruction in block.Instructions)
                {
                    hash.Add(instruction.Opcode);
                    foreach (var operand in instruction.Operands)
                        HashOperand(ref hash, function, operand, vregs, blockIndex);
                }
            }
            return hash.ToHashCode();
        }

        private void HashOperand(
            ref HashCode hash, MirFunction function, MirOperand operand, Dictionary<int, int> vregs,
            Dictionary<MirBlock, int> blockIndex)
        {
            switch (operand)
            {
                case VirtualReg vreg:
                    hash.Add(1);
                    hash.Add(vreg.IsDefinition);
                    hash.Add(Number(vregs, vreg.Id));
                    break;
                case Immediate immediate:
                    hash.Add(2);
                    hash.Add(immediate.Value);
                    break;
                case Symbol symbol:
                    hash.Add(3);
                    hash.Add(symbol.Offset);
                    var target = Resolve(symbol.Name);
                    hash.Add(target == Resolve(function.Name) ? "<self>" : target);
                    break;
                case BlockTarget blockTarget:
                    hash.Add(4);
                    hash.Add(blockIndex[blockTarget.Block]);
                    foreach (var arg in blockTarget.Args)
                        HashOperand(ref hash, function, arg, vregs, blockIndex);
                    break;
                default:
                    hash.Add(operand);
                    break;
            }
        }

        private static int Number(Dictionary<int, int> vregs, int vreg)
        {
            if (!vregs.TryGetValue(vreg, out var number))
                vregs[vreg] = number = vregs.Count;
            return number;
        }

        private static Dictionary<MirBlock, int> BlockIndexes(MirFunction function)
        {
            var result = new Dictionary<MirBlock, int>(function.Blocks.Count);
            for (var i = 0; i < function.Blocks.Count; i++)
                result[function.Blocks[i]] = i;
            return result;
        }

        public bool Equivalent(MirFunction left, MirFunction right)
        {
            if (left.AlwaysInline != right.AlwaysInline
                || !left.ReturnType.Equals(right.ReturnType)
                || !left.ParameterTypes.AsSpan().SequenceEqual(right.ParameterTypes)
                || left.FrameSlots.Count != right.FrameSlots.Count
                || left.Blocks.Count != right.Blocks.Count)
                return false;

            for (var i = 0; i < left.FrameSlots.Count; i++)
                if (left.FrameSlots[i].Index != right.FrameSlots[i].Index
                    || !left.FrameSlots[i].Type.Equals(right.FrameSlots[i].Type))
                    return false;

            var state = new Match(left, right, BlockIndexes(left), BlockIndexes(right));
            for (var b = 0; b < left.Blocks.Count; b++)
            {
                var leftBlock = left.Blocks[b];
                var rightBlock = right.Blocks[b];
                if (leftBlock.Parameters.Count != rightBlock.Parameters.Count
                    || leftBlock.Instructions.Count != rightBlock.Instructions.Count)
                    return false;

                for (var p = 0; p < leftBlock.Parameters.Count; p++)
                    if (!state.BindVReg(leftBlock.Parameters[p], rightBlock.Parameters[p]))
                        return false;

                for (var i = 0; i < leftBlock.Instructions.Count; i++)
                {
                    var leftInstruction = leftBlock.Instructions[i];
                    var rightInstruction = rightBlock.Instructions[i];
                    if (leftInstruction.Opcode != rightInstruction.Opcode
                        || leftInstruction.Operands.Length != rightInstruction.Operands.Length)
                        return false;

                    for (var o = 0; o < leftInstruction.Operands.Length; o++)
                        if (!OperandsMatch(state, leftInstruction.Operands[o], rightInstruction.Operands[o]))
                            return false;
                }
            }
            return true;
        }

        private bool OperandsMatch(Match state, MirOperand left, MirOperand right)
        {
            switch (left, right)
            {
                case (VirtualReg a, VirtualReg b):
                    return a.IsDefinition == b.IsDefinition && state.BindVReg(a.Id, b.Id);
                case (Immediate a, Immediate b):
                    return a.Value == b.Value;
                case (Symbol a, Symbol b):
                    if (a.Offset != b.Offset)
                        return false;
                    var leftTarget = Resolve(a.Name);
                    var rightTarget = Resolve(b.Name);
                    var leftSelf = leftTarget == Resolve(state.Left.Name);
                    var rightSelf = rightTarget == Resolve(state.Right.Name);
                    return leftSelf || rightSelf ? leftSelf && rightSelf : leftTarget == rightTarget;
                case (BlockTarget a, BlockTarget b):
                    if (state.LeftBlocks[a.Block] != state.RightBlocks[b.Block] || a.Args.Length != b.Args.Length)
                        return false;
                    for (var i = 0; i < a.Args.Length; i++)
                        if (!OperandsMatch(state, a.Args[i], b.Args[i]))
                            return false;
                    return true;
                default:
                    return left.GetType() == right.GetType() && left.Equals(right);
            }
        }

        /// <summary>The correspondence built up while comparing two functions in lockstep.</summary>
        private sealed class Match(
            MirFunction left, MirFunction right, Dictionary<MirBlock, int> leftBlocks, Dictionary<MirBlock, int> rightBlocks)
        {
            private readonly Dictionary<int, int> _leftToRight = [];
            private readonly Dictionary<int, int> _rightToLeft = [];

            public MirFunction Left { get; } = left;
            public MirFunction Right { get; } = right;
            public Dictionary<MirBlock, int> LeftBlocks { get; } = leftBlocks;
            public Dictionary<MirBlock, int> RightBlocks { get; } = rightBlocks;

            /// <summary>
            /// Pair two virtual registers: the first sight of either fixes the pairing (one to one), and a
            /// pair must have the same type and value range.
            /// </summary>
            public bool BindVReg(int leftVreg, int rightVreg)
            {
                var hasLeft = _leftToRight.TryGetValue(leftVreg, out var pairedRight);
                var hasRight = _rightToLeft.TryGetValue(rightVreg, out var pairedLeft);
                if (hasLeft || hasRight)
                    return hasLeft && hasRight && pairedRight == rightVreg && pairedLeft == leftVreg;

                if (!Left.GetVRegAnnotation(leftVreg).Equals(Right.GetVRegAnnotation(rightVreg)))
                    return false;

                _leftToRight[leftVreg] = rightVreg;
                _rightToLeft[rightVreg] = leftVreg;
                return true;
            }
        }
    }
}
