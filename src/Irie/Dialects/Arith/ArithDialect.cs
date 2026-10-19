using Irie.Mir;

namespace Irie.Dialects.Arith;

/// <summary>
/// Pure SSA value producers: arith.addi, arith.subi, arith.cmpi &lt;pred&gt; (predicate from
/// ArithCmpPredicate), arith.addi_with_carry (two defs, result + carry_out; three
/// uses, a, b, carry_in: the post-legalization narrow form of a multi-byte add), the
/// bitwise arith.and / or / xor, and the shifts arith.shl / lshr / ashr (constant
/// amount only; the MOS6502 legalizer lowers them to byte moves plus chains of the
/// target's one-bit ops mos6502.shle / lshre, the analogues of llvm-mos
/// G_SHLE / G_LSHRE, which select to asl/rol/lsr/ror).
/// </summary>
/// <remarks>
/// No arith op takes or produces a `ptr` (see the Mem dialect).
/// </remarks>
public sealed class ArithDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "arith";

    public static OpcodeRef OpRef(ArithOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((ArithOp)code) switch
    {
        ArithOp.AddI       => "addi",
        ArithOp.SubI       => "subi",
        ArithOp.CmpI       => "cmpi",
        ArithOp.AddICarry  => "addi_with_carry",
        ArithOp.SubIBorrow => "subi_with_borrow",
        ArithOp.Constant   => "constant",
        ArithOp.Select     => "select",
        ArithOp.Not        => "not",
        ArithOp.And        => "and",
        ArithOp.Or         => "or",
        ArithOp.Xor        => "xor",
        ArithOp.Shl        => "shl",
        ArithOp.LShr       => "lshr",
        ArithOp.AShr       => "ashr",
        ArithOp.MulI       => "muli",
        ArithOp.UDivI      => "udivi",
        ArithOp.SDivI      => "sdivi",
        ArithOp.URemI      => "uremi",
        ArithOp.SRemI      => "sremi",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown arith opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "addi":              code = (ushort)ArithOp.AddI;       return true;
            case "subi":              code = (ushort)ArithOp.SubI;       return true;
            case "cmpi":              code = (ushort)ArithOp.CmpI;       return true;
            case "addi_with_carry":   code = (ushort)ArithOp.AddICarry;  return true;
            case "subi_with_borrow":  code = (ushort)ArithOp.SubIBorrow; return true;
            case "constant":          code = (ushort)ArithOp.Constant;   return true;
            case "select":            code = (ushort)ArithOp.Select;     return true;
            case "not":               code = (ushort)ArithOp.Not;        return true;
            case "and":               code = (ushort)ArithOp.And;       return true;
            case "or":                code = (ushort)ArithOp.Or;        return true;
            case "xor":               code = (ushort)ArithOp.Xor;       return true;
            case "shl":               code = (ushort)ArithOp.Shl;       return true;
            case "lshr":              code = (ushort)ArithOp.LShr;      return true;
            case "ashr":              code = (ushort)ArithOp.AShr;      return true;
            case "muli":              code = (ushort)ArithOp.MulI;      return true;
            case "udivi":             code = (ushort)ArithOp.UDivI;     return true;
            case "sdivi":             code = (ushort)ArithOp.SDivI;     return true;
            case "uremi":             code = (ushort)ArithOp.URemI;     return true;
            case "sremi":             code = (ushort)ArithOp.SRemI;     return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        (ArithOp)code switch
        {
            // cmpi's def is always i1, but its legalization decision is driven
            // by the operand type. Operand layout:
            //   [0]=def i1, [1]=predicate Immediate, [2]=a, [3]=b.
            // Point the legalizer at operand[2] (the first arg vreg).
            ArithOp.CmpI => new DialectInstructionInfo(TypeOperandIndex: 2),
            _ => DialectInstructionInfo.Empty,
        };

    public override bool IsSideEffectFree(ushort code) => true;

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    /// <summary>
    /// arith.cmpi's first use is the predicate (encoded as an Immediate with the
    /// ArithCmpPredicate enum value); render it symbolically (`eq`, `slt`, …)
    /// rather than as a number.
    /// </summary>
    public override bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        if ((ArithOp)code == ArithOp.CmpI && useIndex == 0)
        {
            text = ArithCmpPredicateNames.ToText((ArithCmpPredicate)value);
            return true;
        }
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Legal inline immediates: arith.constant's own value (use 0) and arith.cmpi's
    /// predicate (use 0). Every other arith immediate is an illegal inline value
    /// literal that must be materialised as an arith.constant def.
    /// </summary>
    public override bool IsLegalImmediateOperand(ushort code, int useIndex) =>
        (ArithOp)code switch
        {
            ArithOp.Constant => useIndex == 0,
            ArithOp.CmpI     => useIndex == 0,
            _ => false,
        };

    public override bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        if ((ArithOp)code == ArithOp.CmpI && useIndex == 0
            && ArithCmpPredicateNames.TryFromText(text, out var predicate))
        {
            value = (long)predicate;
            return true;
        }
        value = 0;
        return false;
    }

    internal override void OnRegistered(DialectId id) => Id = id;
}
