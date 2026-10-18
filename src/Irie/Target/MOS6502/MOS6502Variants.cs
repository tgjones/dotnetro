using Irie.Mir;

namespace Irie.Target.MOS6502;

// The operations whose implementation a placement chooses among variants: the
// unpinned ops of MOS6502Op plus the register <-> memory moves a placement adds
// around them.
public enum MOS6502Operation
{
    Adc,
    Sbc,
    And,
    Ora,
    Eor,
    Shl,      // one-bit shift left through the carry (mos6502.shle)
    Lshr,     // one-bit logical shift right through the carry (mos6502.lshre)
    Cmp,      // the compare of mos6502.cmp_br (then the branches on its flags) and of mos6502.cmp (the carry)
    Load,     // register <- the byte a pointer pair or a symbol addresses
    Store,    // the byte a pointer pair or a symbol addresses <- register
    Const,    // register <- constant or address byte
    Reload,   // register <- the home of a value
    Spill,    // home <- register
}

// Where an operand of a variant has to be.
[Flags]
public enum Place
{
    None  = 0,
    A     = 1,
    X     = 2,
    Y     = 4,
    Carry = 8,
    Home  = 16,   // a function-local zero-page byte (absolute memory if layout demotes it)
    Imm   = 32,   // an immediate in the instruction
    Abs   = 64,   // a symbol + offset
    Pair  = 128,  // a zero-page pointer pair addressed with (zp),Y
}

// One instruction of a variant's template, with the operand it takes from the
// operation's memory-side operand (a home, an immediate, a symbol).
public sealed record TemplateInstruction(MOS6502Op Op);

// One way to implement an operation: the register its byte operand lives in, the
// kind of place its other operand lives in, and the instructions that do it.
// Bytes and cycles are derived from those instructions through the opcode tables,
// so what the search would count is exactly what is emitted.
public sealed record Variant(
    MOS6502Operation Operation,
    string           Name,
    Place            Register,
    Place            Memory,
    TemplateInstruction[] Code)
{
    public int Bytes => Code.Sum(i => MOS6502Variants.DescribeInstruction(i.Op).Bytes);
    public int Cycles => Code.Sum(i => MOS6502Variants.DescribeInstruction(i.Op).Cycles);

    // The instructions of the variant with `memory` as the memory-side operand
    // (an Immediate, a Symbol, or the PhysicalReg of a home).
    public IEnumerable<(OpcodeRef Opcode, MirOperand[] Operands)> Instantiate(MirOperand? memory = null)
    {
        foreach (var instruction in Code)
            yield return (MOS6502Dialect.OpRef(instruction.Op), MOS6502Variants.Layout(instruction.Op, memory));
    }
}

// The table of variants. It is the single source of truth for what a placement can
// emit for an operation and what that costs; the naive placement selects from it
// with `Find`.
public static class MOS6502Variants
{
    private const int A = MOS6502Registers.A;
    private const int X = MOS6502Registers.X;
    private const int Y = MOS6502Registers.Y;
    private const int C = MOS6502Registers.C;

    // The scratch pointer pair `(zp),Y` accesses go through.
    public static readonly int PointerLo = MOS6502Registers.RC(18);
    public static readonly int PointerHi = MOS6502Registers.RC(19);

    public static IReadOnlyList<Variant> All { get; } = Build();

    public static IEnumerable<Variant> For(MOS6502Operation operation) =>
        All.Where(v => v.Operation == operation);

    // The variant of `operation` with its byte operand in `register` and the other
    // operand in `memory`; null if the table has none.
    public static Variant? Find(MOS6502Operation operation, Place register, Place memory, string? name = null) =>
        All.FirstOrDefault(v =>
            v.Operation == operation && v.Register == register && v.Memory == memory
            && (name is null || v.Name == name));

    public static Variant Require(MOS6502Operation operation, Place register, Place memory, string? name = null) =>
        Find(operation, register, memory, name)
        ?? throw new NotSupportedException(
            $"MOS6502Variants: no variant of {operation} with the register in {register} and the other operand in {memory}.");

    private static List<Variant> Build()
    {
        var table = new List<Variant>();

        void Add(MOS6502Operation operation, string name, Place register, Place memory, params MOS6502Op[] code) =>
            table.Add(new Variant(operation, name, register, memory, [.. code.Select(o => new TemplateInstruction(o))]));

        // Accumulator arithmetic and logic: the left operand and the result are in A.
        void Alu(MOS6502Operation operation, MOS6502Op imm, MOS6502Op zp, MOS6502Op abs)
        {
            Add(operation, "imm", Place.A, Place.Imm,  imm);
            Add(operation, "zp",  Place.A, Place.Home, zp);
            Add(operation, "abs", Place.A, Place.Abs,  abs);
        }
        Alu(MOS6502Operation.Adc, MOS6502Op.AdcImm, MOS6502Op.AdcZp, MOS6502Op.AdcAbs);
        Alu(MOS6502Operation.Sbc, MOS6502Op.SbcImm, MOS6502Op.SbcZp, MOS6502Op.SbcAbs);
        Alu(MOS6502Operation.And, MOS6502Op.AndImm, MOS6502Op.AndZp, MOS6502Op.AndAbs);
        Alu(MOS6502Operation.Ora, MOS6502Op.OraImm, MOS6502Op.OraZp, MOS6502Op.OraAbs);
        Alu(MOS6502Operation.Eor, MOS6502Op.EorImm, MOS6502Op.EorZp, MOS6502Op.EorAbs);

        // One-bit shifts: `asl` / `lsr` for a known-zero carry in, `rol` / `ror` for
        // a carry in C. The carry out is in C.
        Add(MOS6502Operation.Shl,  "asl", Place.A, Place.None, MOS6502Op.AslAcc);
        Add(MOS6502Operation.Shl,  "rol", Place.A, Place.None, MOS6502Op.RolAcc);
        Add(MOS6502Operation.Lshr, "lsr", Place.A, Place.None, MOS6502Op.LsrAcc);
        Add(MOS6502Operation.Lshr, "ror", Place.A, Place.None, MOS6502Op.RorAcc);

        // Compare: the left operand in A, X or Y (CMP / CPX / CPY).
        Add(MOS6502Operation.Cmp, "cmp.imm", Place.A, Place.Imm,  MOS6502Op.CmpImm);
        Add(MOS6502Operation.Cmp, "cmp.zp",  Place.A, Place.Home, MOS6502Op.CmpZp);
        Add(MOS6502Operation.Cmp, "cmp.abs", Place.A, Place.Abs,  MOS6502Op.CmpAbs);
        Add(MOS6502Operation.Cmp, "cpx.imm", Place.X, Place.Imm,  MOS6502Op.CpxImm);
        Add(MOS6502Operation.Cmp, "cpx.zp",  Place.X, Place.Home, MOS6502Op.CpxZp);
        Add(MOS6502Operation.Cmp, "cpx.abs", Place.X, Place.Abs,  MOS6502Op.CpxAbs);
        Add(MOS6502Operation.Cmp, "cpy.imm", Place.Y, Place.Imm,  MOS6502Op.CpyImm);
        Add(MOS6502Operation.Cmp, "cpy.zp",  Place.Y, Place.Home, MOS6502Op.CpyZp);
        Add(MOS6502Operation.Cmp, "cpy.abs", Place.Y, Place.Abs,  MOS6502Op.CpyAbs);

        // Memory loads and stores: through a symbol, or a pointer pair with the
        // offset in Y.
        Add(MOS6502Operation.Load,  "abs", Place.A, Place.Abs,  MOS6502Op.LdaAbs);
        Add(MOS6502Operation.Load,  "abs", Place.X, Place.Abs,  MOS6502Op.LdxAbs);
        Add(MOS6502Operation.Load,  "abs", Place.Y, Place.Abs,  MOS6502Op.LdyAbs);
        Add(MOS6502Operation.Load,  "indy", Place.A, Place.Pair, MOS6502Op.LdaIndY);
        Add(MOS6502Operation.Store, "abs", Place.A, Place.Abs,  MOS6502Op.StaAbs);
        Add(MOS6502Operation.Store, "abs", Place.X, Place.Abs,  MOS6502Op.StxAbs);
        Add(MOS6502Operation.Store, "abs", Place.Y, Place.Abs,  MOS6502Op.StyAbs);
        Add(MOS6502Operation.Store, "indy", Place.A, Place.Pair, MOS6502Op.StaIndY);

        // Constants and address bytes into a register.
        Add(MOS6502Operation.Const, "imm",   Place.A, Place.Imm, MOS6502Op.LdaImm);
        Add(MOS6502Operation.Const, "imm",   Place.X, Place.Imm, MOS6502Op.LdxImm);
        Add(MOS6502Operation.Const, "imm",   Place.Y, Place.Imm, MOS6502Op.LdyImm);
        Add(MOS6502Operation.Const, "symlo", Place.A, Place.Abs, MOS6502Op.LdaImmSymLo);
        Add(MOS6502Operation.Const, "symhi", Place.A, Place.Abs, MOS6502Op.LdaImmSymHi);
        Add(MOS6502Operation.Const, "symlo", Place.X, Place.Abs, MOS6502Op.LdxImmSymLo);
        Add(MOS6502Operation.Const, "symhi", Place.X, Place.Abs, MOS6502Op.LdxImmSymHi);

        // The carry set or cleared, for a constant carry in.
        Add(MOS6502Operation.Const, "clc", Place.Carry, Place.Imm, MOS6502Op.Clc);
        Add(MOS6502Operation.Const, "sec", Place.Carry, Place.Imm, MOS6502Op.Sec);

        // Moves between a register and the home of a value.
        Add(MOS6502Operation.Reload, "zp", Place.A, Place.Home, MOS6502Op.LdaZp);
        Add(MOS6502Operation.Reload, "zp", Place.X, Place.Home, MOS6502Op.LdxZp);
        Add(MOS6502Operation.Reload, "zp", Place.Y, Place.Home, MOS6502Op.LdyZp);
        Add(MOS6502Operation.Spill,  "zp", Place.A, Place.Home, MOS6502Op.StaZp);
        Add(MOS6502Operation.Spill,  "zp", Place.X, Place.Home, MOS6502Op.StxZp);
        Add(MOS6502Operation.Spill,  "zp", Place.Y, Place.Home, MOS6502Op.StyZp);

        return table;
    }

    // Mnemonic, addressing mode, size and cycles of a pinned op, from the opcode
    // tables.
    public static MOS6502InstructionDescription DescribeInstruction(MOS6502Op op) =>
        MOS6502InstructionInfo.Instance.Get(MOS6502MachineCodeEmitTable.Get(op).OpcodeByte);

    // The operand array a pinned op of this template takes, per the operand
    // layouts the rest of the pipeline already uses for it (defs first). `memory`
    // is the op's immediate / symbol / home operand where it has one.
    public static MirOperand[] Layout(MOS6502Op op, MirOperand? memory)
    {
        static PhysicalReg Def(int reg) => new(reg, IsDefinition: true);
        static PhysicalReg Use(int reg) => new(reg, IsDefinition: false);
        static PhysicalReg ImplicitUse(int reg) => new(reg, IsDefinition: false, IsImplicit: true);
        static PhysicalReg ImplicitDef(int reg) => new(reg, IsDefinition: true, IsImplicit: true);

        MirOperand Memory() => memory ?? throw new ArgumentException($"{op} needs its memory operand.");

        // A home spelled as a definition, for the stores that write it.
        MirOperand HomeDef() => Memory() is PhysicalReg home
            ? home with { IsDefinition = true }
            : throw new ArgumentException($"{op} needs a home (a PhysicalReg) operand.");

        return op switch
        {
            MOS6502Op.LdaImm or MOS6502Op.LdaZp or MOS6502Op.LdaAbs
                or MOS6502Op.LdaImmSymLo or MOS6502Op.LdaImmSymHi => [Def(A), Memory()],
            MOS6502Op.LdxImm or MOS6502Op.LdxZp or MOS6502Op.LdxAbs
                or MOS6502Op.LdxImmSymLo or MOS6502Op.LdxImmSymHi => [Def(X), Memory()],
            MOS6502Op.LdyImm or MOS6502Op.LdyZp or MOS6502Op.LdyAbs => [Def(Y), Memory()],

            MOS6502Op.StaZp => [HomeDef(), Use(A)],
            MOS6502Op.StxZp => [HomeDef(), Use(X)],
            MOS6502Op.StyZp => [HomeDef(), Use(Y)],
            MOS6502Op.StaAbs => [Use(A), Memory()],
            MOS6502Op.StxAbs => [Use(X), Memory()],
            MOS6502Op.StyAbs => [Use(Y), Memory()],

            MOS6502Op.LdaIndY => [Def(A), Use(PointerLo), ImplicitUse(PointerHi), ImplicitUse(Y)],
            MOS6502Op.StaIndY => [Use(PointerLo), Use(A), ImplicitUse(PointerHi), ImplicitUse(Y)],

            MOS6502Op.AdcImm or MOS6502Op.AdcZp or MOS6502Op.AdcAbs
                or MOS6502Op.SbcImm or MOS6502Op.SbcZp or MOS6502Op.SbcAbs
                => [Def(A), Def(C), Use(A), Memory(), Use(C)],

            MOS6502Op.AndImm or MOS6502Op.AndZp or MOS6502Op.AndAbs
                or MOS6502Op.OraImm or MOS6502Op.OraZp or MOS6502Op.OraAbs
                or MOS6502Op.EorImm or MOS6502Op.EorZp or MOS6502Op.EorAbs
                => [Def(A), Use(A), Memory()],

            MOS6502Op.CmpImm or MOS6502Op.CmpZp or MOS6502Op.CmpAbs
                => [Use(A), Memory(), ImplicitDef(MOS6502Registers.N), ImplicitDef(MOS6502Registers.Z), ImplicitDef(C)],
            MOS6502Op.CpxImm or MOS6502Op.CpxZp or MOS6502Op.CpxAbs
                => [Use(X), Memory(), ImplicitDef(MOS6502Registers.N), ImplicitDef(MOS6502Registers.Z), ImplicitDef(C)],
            MOS6502Op.CpyImm or MOS6502Op.CpyZp or MOS6502Op.CpyAbs
                => [Use(Y), Memory(), ImplicitDef(MOS6502Registers.N), ImplicitDef(MOS6502Registers.Z), ImplicitDef(C)],

            MOS6502Op.AslAcc or MOS6502Op.LsrAcc => [Def(A), Def(C), Use(A)],
            MOS6502Op.RolAcc or MOS6502Op.RorAcc => [Def(A), Def(C), Use(A), Use(C)],

            MOS6502Op.Clc or MOS6502Op.Sec => [Def(C)],

            _ => throw new NotSupportedException($"MOS6502Variants.Layout: no operand layout for {op}."),
        };
    }
}
