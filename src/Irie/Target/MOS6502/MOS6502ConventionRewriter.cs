using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

/// <summary>
/// Gives a function a calling convention other than the default one, by renaming the
/// registers of its ABI copies: the copies out of the argument registers at its entry,
/// the copies into the result registers before each return, and, in every function that
/// calls it, the copies into the argument registers before the `jsr` and out of the
/// result registers after it. The shapes are those MOS6502CallLowering emits, so this
/// runs on MIR that still has them (the placement has not rewritten the copies yet).
/// </summary>
/// <remarks>
/// A convention can only name registers an ABI copy can use (A, X and the argument
/// zero-page registers), one per byte, and cannot move a byte to or from the stack.
/// </remarks>
internal static class MOS6502ConventionRewriter
{
    private static readonly MOS6502CallLowering Default = new();

    /// <summary>The registers a convention may use for an argument or a result byte.</summary>
    internal static bool IsConventionRegister(int register) =>
        register == MOS6502Registers.A || register == MOS6502Registers.X
        || (register >= MOS6502Registers.RC(2) && register <= MOS6502Registers.RC(15));

    internal static FunctionConvention DefaultFor(MirFunction function) =>
        Default.DefaultConvention(
            function.ParameterTypes.Sum(t => t.SizeInBits / 8),
            function.ReturnType is VoidType ? 0 : function.ReturnType.SizeInBits / 8);

    internal static FunctionConvention CurrentOf(MirFunction function) =>
        function.Convention ?? DefaultFor(function);

    /// <summary>
    /// Whether `convention` is one the rewriter can apply to `function`: the same byte
    /// counts as its signature, every byte in a convention register, no register twice
    /// among the arguments or among the results.
    /// </summary>
    internal static bool IsValidFor(FunctionConvention convention, MirFunction function)
    {
        var current = DefaultFor(function);
        return convention.ArgBytes.Count == current.ArgBytes.Count
            && convention.ReturnBytes.Count == current.ReturnBytes.Count
            && convention.ArgBytes.All(IsConventionRegister)
            && convention.ReturnBytes.All(IsConventionRegister)
            && convention.ArgBytes.Distinct().Count() == convention.ArgBytes.Count
            && convention.ReturnBytes.Distinct().Count() == convention.ReturnBytes.Count;
    }

    /// <summary>
    /// Whether the ABI copies of `callee` and of every call to it have the shapes the
    /// rewriter renames: the function is hand-written, or something has moved copies
    /// away from the call, when they do not.
    /// </summary>
    internal static bool CanRewrite(MirModule module, MirFunction callee)
    {
        var current = CurrentOf(callee);
        if (current.ArgBytes.Any(r => !IsConventionRegister(r)) || current.ReturnBytes.Any(r => !IsConventionRegister(r)))
            return false;
        var none = new Dictionary<int, int>();
        if (RenameEntry(callee, none) != current.ArgBytes.Count) return false;
        var returns = current.ReturnBytes.ToHashSet();
        foreach (var block in callee.Blocks)
            if (RenameReturns(block, none, returns) is { } seen && seen != current.ReturnBytes.Count)
                return false;
        var args = current.ArgBytes.ToHashSet();
        foreach (var caller in module.Functions)
            foreach (var block in caller.Blocks)
                for (var i = 0; i < block.Instructions.Count; i++)
                    if (IsCallTo(block.Instructions[i], callee.Name)
                        && (RenameArguments(block, i, none, args) != current.ArgBytes.Count
                            || RenameResults(block, i, none, returns) > current.ReturnBytes.Count))
                        return false;
        return true;
    }

    /// <summary>The number of calls to `callee` in the module.</summary>
    internal static int CountCalls(MirModule module, MirFunction callee) =>
        module.Functions.Sum(f => f.Blocks.Sum(b => b.Instructions.Count(i => IsCallTo(i, callee.Name))));

    /// <summary>
    /// Moves the arguments and results of `callee` to `chosen` in the callee alone. A
    /// trial of a convention does this, measures the callee and takes it back; the calls
    /// follow in ApplyToCalls once the convention is final.
    /// </summary>
    internal static void ApplyToCallee(MirFunction callee, FunctionConvention chosen)
    {
        if (!IsValidFor(chosen, callee))
            throw new InvalidOperationException($"Convention {chosen} does not fit @{callee.Name}.");
        var current = CurrentOf(callee);

        RenameEntry(callee, Renaming(current.ArgBytes, chosen.ArgBytes));
        var returnMap = Renaming(current.ReturnBytes, chosen.ReturnBytes);
        var returns = current.ReturnBytes.ToHashSet();
        foreach (var block in callee.Blocks)
            RenameReturns(block, returnMap, returns);
        callee.Convention = chosen;
    }

    /// <summary>
    /// Moves the arguments and results of every call to `callee` from `current` to
    /// `chosen`. The callee must not call itself, directly or through a cycle.
    /// </summary>
    internal static void ApplyToCalls(MirModule module, MirFunction callee, FunctionConvention current, FunctionConvention chosen)
    {
        var argMap = Renaming(current.ArgBytes, chosen.ArgBytes);
        var returnMap = Renaming(current.ReturnBytes, chosen.ReturnBytes);
        var args = current.ArgBytes.ToHashSet();
        var returns = current.ReturnBytes.ToHashSet();
        foreach (var caller in module.Functions)
            foreach (var block in caller.Blocks)
                for (var i = 0; i < block.Instructions.Count; i++)
                    if (IsCallTo(block.Instructions[i], callee.Name))
                    {
                        RenameArguments(block, i, argMap, args);
                        var call = block.Instructions[i];
                        call.Operands = call.Operands
                            .Select(o => o is PhysicalReg { IsDefinition: false, IsImplicit: true } p && argMap.TryGetValue(p.Id, out var renamed)
                                ? p with { Id = renamed } : o)
                            .ToArray();
                        RenameResults(block, i, returnMap, returns);
                    }
    }

    /// <summary>Both at once, for a convention that is not tried first.</summary>
    internal static void Apply(MirModule module, MirFunction callee, FunctionConvention chosen)
    {
        var current = CurrentOf(callee);
        if (current.SameAs(chosen)) return;
        ApplyToCallee(callee, chosen);
        ApplyToCalls(module, callee, current, chosen);
    }

    /// <summary>
    /// Checks, once the functions are placed, that nothing reads an argument register
    /// outside the convention of its function: not the function itself, and not the
    /// `jsr` of any call to it, which has to hand over exactly the argument registers.
    /// </summary>
    internal static void Verify(MirModule module)
    {
        var byName = module.Functions.Where(f => f.Convention is not null).ToDictionary(f => f.Name);
        foreach (var function in module.Functions)
        {
            if (function.Convention is { } convention)
            {
                var allowed = convention.ArgBytes.ToHashSet();
                foreach (var register in function.Blocks[0].LiveIns.Where(IsConventionRegister))
                    if (!allowed.Contains(register))
                        throw new InvalidOperationException(
                            $"@{function.Name} reads register {register} on entry, which its convention ({convention}) does not pass an argument in.");
            }
            foreach (var block in function.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr.Opcode == MOS6502Dialect.OpRef(MOS6502Op.JsrAbs) && instr.Operands[0] is Symbol s
                        && byName.TryGetValue(s.Name, out var callee))
                    {
                        var passed = instr.Operands.OfType<PhysicalReg>()
                            .Where(p => !p.IsDefinition && p.IsImplicit).Select(p => p.Id).ToHashSet();
                        if (!passed.SetEquals(callee.Convention!.ArgBytes))
                            throw new InvalidOperationException(
                                $"@{function.Name} calls @{callee.Name} with registers [{string.Join(", ", passed.Order())}], but its convention is {callee.Convention}.");
                    }
        }
    }

    private static Dictionary<int, int> Renaming(IReadOnlyList<int> from, IReadOnlyList<int> to)
    {
        var map = new Dictionary<int, int>();
        for (var k = 0; k < from.Count; k++)
            if (from[k] != to[k])
                map[from[k]] = to[k];
        return map;
    }

    private static bool IsCallTo(MirInstruction instr, string name) =>
        instr.Opcode == MOS6502Dialect.OpRef(MOS6502Op.JsrAbs)
        && instr.Operands[0] is Symbol s && s.Name == name;

    private static bool IsCopy(MirInstruction instr) =>
        instr.Opcode == PseudoDialect.OpRef(PseudoOp.Copy);

    /// <summary>A pseudo op that only reshapes values: it may sit between the ABI copies.</summary>
    private static bool IsReshape(MirInstruction instr) =>
        instr.Opcode == PseudoDialect.OpRef(PseudoOp.Merge)
        || instr.Opcode == PseudoDialect.OpRef(PseudoOp.Unmerge)
        || (IsCopy(instr) && instr.Operands.All(o => o is VirtualReg));

    private static bool CopiesFromRegister(MirInstruction instr, out int register)
    {
        register = 0;
        if (!IsCopy(instr) || instr.Operands.Length != 2
            || instr.Operands[1] is not PhysicalReg { IsDefinition: false } source) return false;
        register = source.Id;
        return true;
    }

    private static bool CopiesToRegister(MirInstruction instr, out int register)
    {
        register = 0;
        if (!IsCopy(instr) || instr.Operands.Length != 2
            || instr.Operands[0] is not PhysicalReg { IsDefinition: true } target) return false;
        register = target.Id;
        return true;
    }

    private static void RenameSource(MirInstruction copy, int register) =>
        copy.Operands = [copy.Operands[0], new PhysicalReg(register, IsDefinition: false)];

    private static void RenameTarget(MirInstruction copy, int register) =>
        copy.Operands = [new PhysicalReg(register, IsDefinition: true), copy.Operands[1]];

    // Each of these renames the ABI copies it finds through `map` and returns how many
    // there were, so with an empty map it only counts. (A call whose result is unused has
    // fewer result copies than the callee has result bytes.)

    private static int RenameEntry(MirFunction callee, Dictionary<int, int> map)
    {
        var entry = callee.Blocks[0];
        for (var k = 0; k < entry.LiveIns.Count; k++)
            if (map.TryGetValue(entry.LiveIns[k], out var renamed))
                entry.LiveIns[k] = renamed;

        // The copies out of the argument registers lead the block, among reshaping ops.
        var seen = 0;
        foreach (var instr in entry.Instructions)
        {
            if (CopiesFromRegister(instr, out var register))
            {
                seen++;
                if (map.TryGetValue(register, out var renamed)) RenameSource(instr, renamed);
            }
            else if (!IsReshape(instr)) break;
        }
        return seen;
    }

    /// <summary>Null for a block that does not return.</summary>
    private static int? RenameReturns(MirBlock block, Dictionary<int, int> map, HashSet<int> registers)
    {
        var instrs = block.Instructions;
        if (instrs.Count == 0 || instrs[^1].Opcode != MOS6502Dialect.OpRef(MOS6502Op.Rts)
            && instrs[^1].Opcode != PseudoDialect.OpRef(PseudoOp.Return)) return null;

        var seen = 0;
        for (var i = instrs.Count - 2; i >= 0; i--)
        {
            if (CopiesToRegister(instrs[i], out var register) && registers.Contains(register))
            {
                seen++;
                if (map.TryGetValue(register, out var renamed)) RenameTarget(instrs[i], renamed);
            }
            else if (!IsReshape(instrs[i])) break;
        }
        return seen;
    }

    private static int RenameArguments(MirBlock block, int call, Dictionary<int, int> map, HashSet<int> registers)
    {
        var instrs = block.Instructions;
        var seen = 0;
        for (var i = call - 1; i >= 0; i--)
        {
            if (CopiesToRegister(instrs[i], out var register) && registers.Contains(register))
            {
                seen++;
                if (map.TryGetValue(register, out var renamed)) RenameTarget(instrs[i], renamed);
            }
            else if (!IsReshape(instrs[i])) break;
        }
        return seen;
    }

    private static int RenameResults(MirBlock block, int call, Dictionary<int, int> map, HashSet<int> registers)
    {
        var instrs = block.Instructions;
        var seen = 0;
        for (var i = call + 1; i < instrs.Count; i++)
        {
            if (CopiesFromRegister(instrs[i], out var register) && registers.Contains(register))
            {
                seen++;
                if (map.TryGetValue(register, out var renamed)) RenameSource(instrs[i], renamed);
            }
            else if (!IsReshape(instrs[i])) break;
        }
        return seen;
    }
}
