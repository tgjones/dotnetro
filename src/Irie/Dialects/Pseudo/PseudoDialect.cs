using Irie.Mir;

namespace Irie.Dialects.Pseudo;

/// <summary>
/// Scaffolding ops that later passes lower or expand:
///   - pseudo.copy: the universal copy. vreg -&gt; vreg before selection, mixed during
///     selection, physreg -&gt; physreg after placement; PseudoExpansionPass turns it
///     into target moves last.
///   - pseudo.merge / pseudo.unmerge: artifact ops (join / split a wide value into
///     bytes) folded by the legalizer's artifact combiner.
///   - pseudo.return: the void return shell AbiLoweringPass emits; it survives to
///     instruction selection, which lowers it to the target's return op.
/// </summary>
public sealed class PseudoDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "pseudo";

    public static OpcodeRef OpRef(PseudoOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Copy    => "copy",
        PseudoOp.Merge   => "merge",
        PseudoOp.Unmerge => "unmerge",
        PseudoOp.Extract => "extract",
        PseudoOp.Insert  => "insert",
        PseudoOp.Return  => "return",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown pseudo opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "copy":    code = (ushort)PseudoOp.Copy;    return true;
            case "merge":   code = (ushort)PseudoOp.Merge;   return true;
            case "unmerge": code = (ushort)PseudoOp.Unmerge; return true;
            case "extract": code = (ushort)PseudoOp.Extract; return true;
            case "insert":  code = (ushort)PseudoOp.Insert;  return true;
            case "return":  code = (ushort)PseudoOp.Return;  return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    public override bool IsSideEffectFree(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Copy    => true,
        PseudoOp.Merge   => true,
        PseudoOp.Unmerge => true,
        PseudoOp.Extract => true,
        PseudoOp.Insert  => true,
        PseudoOp.Return  => false,
        _ => false,
    };

    public override bool IsTerminator(ushort code) => ((PseudoOp)code) == PseudoOp.Return;

    public override bool IsArtifact(ushort code) => ((PseudoOp)code) switch
    {
        PseudoOp.Merge   => true,
        PseudoOp.Unmerge => true,
        PseudoOp.Extract => true,
        PseudoOp.Insert  => true,
        _ => false,
    };

    /// <summary>
    /// Legal inline immediates: structural attributes (bit offsets), not value
    /// literals. pseudo.extract's bit offset (use 1), pseudo.insert's bit offset
    /// (use 2).
    /// </summary>
    public override bool IsLegalImmediateOperand(ushort code, int useIndex) =>
        (PseudoOp)code switch
        {
            PseudoOp.Extract => useIndex == 1,
            PseudoOp.Insert  => useIndex == 2,
            _ => false,
        };

    internal override void OnRegistered(DialectId id) => Id = id;
}
