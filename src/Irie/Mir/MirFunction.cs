
namespace Irie.Mir;

public sealed class MirFunction(string name, IRType[] paramTypes, IRType returnType)
{
    public string Name { get; } = name;

    /// <summary>
    /// Signature is preserved by every pass — even after ABI lowering replaces
    /// entry-block parameters with pseudo.copy from physregs.
    /// </summary>
    public IRType[] ParameterTypes { get; } = paramTypes;
    public IRType   ReturnType    { get; } = returnType;

    public List<MirBlock> Blocks { get; } = [];

    /// <summary>
    /// Where the arguments arrive and the results leave, once something has chosen it
    /// for this function; null while the function uses the target's default convention
    /// for its signature. The ABI copies of the function and of every call to it follow it.
    /// </summary>
    public Irie.Target.FunctionConvention? Convention { get; set; }

    /// <summary>
    /// `func noinline @f`: the function stays a callable unit of its own, which the InliningPass
    /// neither inlines nor removes (for a test of calls, or irie-exec's function under test).
    /// Not part of the binary format.
    /// </summary>
    public bool NoInline { get; set; }

    /// <summary>
    /// Interprocedural property set by ReentrancyAnalysis: true iff this function
    /// can never be active on the call stack more than once at a time (its call-
    /// graph SCC is a singleton, it doesn't self-recurse, and it isn't reachable
    /// from an interrupt handler). A non-reentrant function may use static memory
    /// (e.g. fixed zero-page addresses) for its locals, because there is never a
    /// second live activation whose locals would alias the first's. Default false
    /// (conservative) until the analysis proves otherwise.
    /// </summary>
    public bool IsNonReentrant { get; set; }

    /// <summary>
    /// Address-taken / value-type locals that the frontend has reserved as
    /// .bss-style frame slots. The FrameLoweringPass converts each entry to a
    /// MirGlobal (zero-init) on the module, and rewrites each mem.frame_addr
    /// use to a mem.symbol against that global's name.
    /// </summary>
    public List<FrameSlot> FrameSlots { get; } = [];

    private int _nextVirtualRegister;
    private readonly Dictionary<int, VRegAnnotation> _annotations = [];

    public int CreateVirtualRegister(IRType type)
    {
        var vreg = _nextVirtualRegister++;
        _annotations[vreg] = new TypedVReg(type);
        return vreg;
    }

    public int CreateVirtualRegisterInClass(int classId, string className)
    {
        var vreg = _nextVirtualRegister++;
        _annotations[vreg] = new ClassedVReg(classId, className);
        return vreg;
    }

    public VRegAnnotation GetVRegAnnotation(int vreg) => _annotations[vreg];

    /// <summary>Sets the value range of an integer vreg that has a type annotation (null clears it).</summary>
    public void SetValueRange(int vreg, ValueRange? range)
    {
        if (_annotations[vreg] is TypedVReg typed)
            _annotations[vreg] = typed with { Range = range };
    }

    public bool TryGetVRegAnnotation(int vreg, out VRegAnnotation annotation) =>
        _annotations.TryGetValue(vreg, out annotation!);

    /// <summary>
    /// Live virtual-register IDs in dictionary insertion order — stable across
    /// round-trips because both the text parser and the binary reader register
    /// vregs in the order they appear.
    /// </summary>
    public IReadOnlyCollection<int> VirtualRegisterIds => _annotations.Keys;

    /// <summary>
    /// Replace a vreg's TypedVReg entry with a ClassedVReg. Called by
    /// instruction selection when it commits a vreg to a register class.
    /// </summary>
    public void ReclassifyVirtualRegister(int vreg, int classId, string className)
    {
        _annotations[vreg] = new ClassedVReg(classId, className);
    }

    /// <summary>Used by parsers to register a vreg with a caller-supplied ID.</summary>
    internal void RegisterVirtualRegister(int id, VRegAnnotation annotation)
    {
        _annotations[id] = annotation;
        if (id >= _nextVirtualRegister)
            _nextVirtualRegister = id + 1;
    }

    /// <summary>
    /// What a trial transformation of the function (a placement run to measure its cost)
    /// needs to be undone: the blocks with their instructions and operands, the vreg
    /// table and the frame slots.
    /// </summary>
    internal sealed class SavedState
    {
        internal required List<MirBlock> Blocks;
        internal required List<(MirBlock Block, List<int> Parameters, List<int> LiveIns,
            List<(MirInstruction Instruction, MirOperand[] Operands)> Instructions)> BlockStates;
        internal required Dictionary<int, VRegAnnotation> Annotations;
        internal required int NextVirtualRegister;
        internal required List<(FrameSlot Slot, int StackId, int Offset)> Slots;
        internal required Irie.Target.FunctionConvention? Convention;
    }

    internal SavedState SaveState() => new()
    {
        Blocks = [.. Blocks],
        BlockStates = [.. Blocks.Select(b => (b, new List<int>(b.Parameters), new List<int>(b.LiveIns),
            b.Instructions.Select(i => (i, (MirOperand[])i.Operands.Clone())).ToList()))],
        Annotations = new Dictionary<int, VRegAnnotation>(_annotations),
        NextVirtualRegister = _nextVirtualRegister,
        Slots = [.. FrameSlots.Select(s => (s, s.StackId, s.Offset))],
        Convention = Convention,
    };

    internal void RestoreState(SavedState state)
    {
        Blocks.Clear();
        Blocks.AddRange(state.Blocks);
        foreach (var (block, parameters, liveIns, instructions) in state.BlockStates)
        {
            block.Parameters.Clear();
            block.Parameters.AddRange(parameters);
            block.LiveIns.Clear();
            block.LiveIns.AddRange(liveIns);
            block.Instructions.Clear();
            foreach (var (instruction, operands) in instructions)
            {
                instruction.Operands = operands;
                instruction.Parent = block;
                block.Instructions.Add(instruction);
            }
        }
        _annotations.Clear();
        foreach (var (vreg, annotation) in state.Annotations) _annotations[vreg] = annotation;
        _nextVirtualRegister = state.NextVirtualRegister;
        FrameSlots.Clear();
        foreach (var (slot, stackId, offset) in state.Slots)
        {
            slot.StackId = stackId;
            slot.Offset = offset;
            FrameSlots.Add(slot);
        }
        Convention = state.Convention;
        RebuildCfg();
    }

    /// <summary>Drop the vreg table after RA has replaced every VirtualReg with PhysicalReg.</summary>
    public void ClearVRegAnnotations() => _annotations.Clear();

    public MirBlock CreateBlock(Action<MirBlock>? configure = null)
    {
        var block = new MirBlock();
        block.Parent = this;
        configure?.Invoke(block);
        Blocks.Add(block);
        return block;
    }

    /// <summary>Linear scan for the unique defining instruction of a virtual register.</summary>
    public MirInstruction? GetDefinition(int vreg)
    {
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualReg v && v.IsDefinition && v.Id == vreg)
                        return instr;
        return null;
    }

    /// <summary>
    /// Every instruction that defines `vreg`, in program order. After PHI
    /// elimination a block-parameter vreg is defined once per incoming edge (the
    /// edge copies), so "the" definition is not unique past that point.
    /// </summary>
    public List<MirInstruction> GetDefinitions(int vreg)
    {
        var defs = new List<MirInstruction>();
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    if (operand is VirtualReg v && v.IsDefinition && v.Id == vreg)
                    {
                        defs.Add(instr);
                        break;
                    }
        return defs;
    }

    public int GetUseCount(int vreg)
    {
        var count = 0;
        foreach (var block in Blocks)
            foreach (var instr in block.Instructions)
                foreach (var operand in instr.Operands)
                    count += CountUsesIn(operand, vreg);
        return count;
    }

    private static int CountUsesIn(MirOperand operand, int vreg) => operand switch
    {
        VirtualReg v when !v.IsDefinition && v.Id == vreg => 1,
        BlockTarget bt => CountUsesInArgs(bt.Args, vreg),
        _ => 0,
    };

    private static int CountUsesInArgs(MirOperand[] args, int vreg)
    {
        var count = 0;
        foreach (var arg in args)
            count += CountUsesIn(arg, vreg);
        return count;
    }

    /// <summary>
    /// SSA RAUW: rewrite every non-def use of oldVreg to point at newVreg,
    /// including uses nested inside BlockTarget.Args.
    /// </summary>
    public void ReplaceAllUsesOfRegister(int oldVreg, int newVreg)
    {
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var operands = instr.Operands;
                for (var i = 0; i < operands.Length; i++)
                    operands[i] = ReplaceUseIn(operands[i], oldVreg, newVreg);
            }
        }
    }

    private static MirOperand ReplaceUseIn(MirOperand operand, int oldVreg, int newVreg) => operand switch
    {
        VirtualReg v when !v.IsDefinition && v.Id == oldVreg => v with { Id = newVreg },
        BlockTarget bt => bt with { Args = ReplaceUsesInArgs(bt.Args, oldVreg, newVreg) },
        _ => operand,
    };

    private static MirOperand[] ReplaceUsesInArgs(MirOperand[] args, int oldVreg, int newVreg)
    {
        var rewritten = new MirOperand[args.Length];
        for (var i = 0; i < args.Length; i++)
            rewritten[i] = ReplaceUseIn(args[i], oldVreg, newVreg);
        return rewritten;
    }

    /// <summary>
    /// Trivially dead: side-effect-free opcode, no physical-register defs, and
    /// every virtual-register def has zero uses.
    /// </summary>
    public bool IsTriviallyDead(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        if (!dialect.IsSideEffectFree(instr.Opcode.Code))
            return false;

        var hasVRegDef = false;
        foreach (var operand in instr.Operands)
        {
            switch (operand)
            {
                case PhysicalReg p when p.IsDefinition:
                    return false; // phys-reg def is observable
                case VirtualReg v when v.IsDefinition:
                    hasVRegDef = true;
                    if (GetUseCount(v.Id) > 0)
                        return false;
                    break;
            }
        }

        return hasVRegDef;
    }

    /// <summary>Rebuild predecessor/successor lists from terminator BlockTarget operands.</summary>
    public void RebuildCfg()
    {
        foreach (var block in Blocks)
        {
            block.Predecessors.Clear();
            block.Successors.Clear();
        }

        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (var op in instr.Operands)
                {
                    if (op is not BlockTarget target) continue;
                    var succ = target.Block;
                    if (!block.Successors.Contains(succ))
                        block.Successors.Add(succ);
                    if (!succ.Predecessors.Contains(block))
                        succ.Predecessors.Add(block);
                }
            }
        }
    }
}
