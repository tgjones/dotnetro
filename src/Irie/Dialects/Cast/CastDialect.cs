using Irie.Mir;

namespace Irie.Dialects.Cast;

/// <summary>
/// The `cast` dialect carries typed integer conversions emitted by the IL
/// frontend before the legalizer turns them into byte-level slice / merge
/// artifacts.
/// </summary>
/// <remarks>
/// cast.trunc / zext / sext convert between integers; cast.ptrtoint / inttoptr convert
/// between `ptr` and `i16` (same width only). A null pointer is `inttoptr (constant 0)`;
/// a pointer compare is ptrtoint on both sides plus an integer cmpi. The IL front end
/// treats native int (IntPtr, conv.i) as an i16 integer and inserts these casts where
/// IL uses one as an address.
/// </remarks>
public sealed class CastDialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "cast";

    public static OpcodeRef OpRef(CastOp op) => new(Id, (ushort)op);

    public override string GetOpName(ushort code) => ((CastOp)code) switch
    {
        CastOp.Trunc => "trunc",
        CastOp.Zext  => "zext",
        CastOp.Sext  => "sext",
        CastOp.PtrToInt => "ptrtoint",
        CastOp.IntToPtr => "inttoptr",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown cast opcode {code}."),
    };

    public override bool TryParseOp(string name, out ushort code)
    {
        switch (name)
        {
            case "trunc": code = (ushort)CastOp.Trunc; return true;
            case "zext":  code = (ushort)CastOp.Zext;  return true;
            case "sext":  code = (ushort)CastOp.Sext;  return true;
            case "ptrtoint": code = (ushort)CastOp.PtrToInt; return true;
            case "inttoptr": code = (ushort)CastOp.IntToPtr; return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        DialectInstructionInfo.Empty;

    /// <summary>
    /// All cast ops are pure data motions on their input vreg — no memory
    /// touched, no flags set. DCE can drop them when their def is unused.
    /// </summary>
    public override bool IsSideEffectFree(ushort code) => true;

    public override bool IsTerminator(ushort code) => false;

    public override bool IsArtifact(ushort code) => false;

    internal override void OnRegistered(DialectId id) => Id = id;
}
