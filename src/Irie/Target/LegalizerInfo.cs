using Irie.Mir;

namespace Irie.Target;

/// <summary>
/// MIR-shaped legalizer information. Per (opcode, type) pair, the target reports
/// whether the op is legal, must be narrowed, or is unsupported. The legalizer
/// queries this for every instruction it sees and drives the corresponding
/// transformation (only NarrowScalar today). Mirrors the old
/// Irie.CodeGen.LegalizerInfo, but talks in OpcodeRef + Mir types.
/// </summary>
public abstract class LegalizerInfo
{
    /// <summary>
    /// Returns the legality of an (opcode, type) combination. The type is the
    /// annotation of the instruction's first def operand.
    /// </summary>
    public abstract LegalityAction GetAction(OpcodeRef opcode, IRType type);

    /// <summary>Returns the type to narrow to for a NarrowScalar action.</summary>
    public abstract IRType GetNarrowType(OpcodeRef opcode, IRType fromType);

    /// <summary>
    /// Hook for the Custom legality action: the target rewrites the instruction
    /// via the builder (and removes the original). The builder is positioned at
    /// the instruction on entry. Throws if the target doesn't recognise the op.
    /// </summary>
    public virtual void LegalizeCustom(MirInstruction instruction, MirBuilder builder)
    {
        throw new NotImplementedException(
            "LegalizerInfo.LegalizeCustom must be overridden by the target for any opcode that " +
            "returns LegalityAction.Custom.");
    }
}

public enum LegalityAction
{
    Legal,
    NarrowScalar,
    /// <summary>
    /// Target-supplied transformation: the legalizer calls
    /// LegalizerInfo.LegalizeCustom which mutates the IR via the builder.
    /// </summary>
    Custom,
    Unsupported,
}
