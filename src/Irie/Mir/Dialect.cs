namespace Irie.Mir;

/// <summary>
/// A dialect bundles a prefix string with an opcode enum and per-opcode
/// metadata. Dialects are looked up by ID (an opaque DialectId handed out by
/// DialectRegistry.Register at registration time) or by their prefix.
/// </summary>
public abstract class Dialect
{
    /// <summary>
    /// Populated by DialectRegistry.Register. Stays at default(DialectId)
    /// until then; do not look up a dialect by Id before it's registered.
    /// </summary>
    public DialectId Id { get; internal set; }

    /// <summary>Lowercase short name printed before each opcode (e.g. "arith", "core").</summary>
    public abstract string Prefix { get; }

    /// <summary>Op name without the dialect prefix (e.g. "addi", "cmpi").</summary>
    public abstract string GetOpName(ushort code);

    /// <summary>Reverse of GetOpName.</summary>
    public abstract bool TryParseOp(string name, out ushort code);

    /// <summary>
    /// Per-opcode metadata. Returns DialectInstructionInfo.Empty for opcodes
    /// that have no operand classes / tied operands / implicit operands.
    /// </summary>
    public abstract DialectInstructionInfo GetInstructionInfo(ushort code);

    /// <summary>True when this opcode produces no observable side effects (drives DCE).</summary>
    public abstract bool IsSideEffectFree(ushort code);

    /// <summary>
    /// True when an instruction of this opcode that names a symbol may be a call to
    /// that symbol (as opposed to taking its address or reading it as data). The
    /// call graph treats those that do as calls. Default: any op with side effects.
    /// </summary>
    public virtual bool IsCall(ushort code) => !IsSideEffectFree(code);

    /// <summary>True when this opcode terminates a basic block.</summary>
    public abstract bool IsTerminator(ushort code);

    /// <summary>
    /// True when this opcode returns from the function (e.g. `mos6502.rts`).
    /// Mirrors LLVM's MCInstrDesc::isReturn — a generic, target-agnostic query
    /// used by passes such as TailDuplicationPass. Scaffolding/non-target
    /// dialects have no return op, so the default is false.
    /// </summary>
    public virtual bool IsReturn(ushort code) => false;

    /// <summary>
    /// True when this opcode is a type-shape artifact (e.g. pseudo.merge /
    /// pseudo.unmerge) that the legalization artifact combiner can fold.
    /// </summary>
    public abstract bool IsArtifact(ushort code);

    /// <summary>
    /// Optional symbolic rendering for an Immediate use operand. Returns true
    /// and sets `text` if the dialect wants to render the immediate symbolically
    /// (e.g. `arith.cmpi`'s first use is a predicate enum value rendered as
    /// `slt`, `eq`, etc.); returns false to fall back to numeric formatting.
    /// `useIndex` is the index of the operand among the instruction's uses
    /// (after the defs).
    /// </summary>
    public virtual bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// True if an Immediate at this use-operand index is a legal structural
    /// attribute (cmpi predicate, mem offset/count, …) or arith.constant's value —
    /// as opposed to an illegal inline value literal that must be an arith.constant
    /// def. `useIndex` is 0-based among USE operands (defs excluded), matching the
    /// TryFormatImmediateUse convention. Default: no legal immediate operands.
    /// </summary>
    public virtual bool IsLegalImmediateOperand(ushort code, int useIndex) => false;

    /// <summary>
    /// Inverse of TryFormatImmediateUse. Returns true and sets `value` when the
    /// dialect can interpret `text` as a symbolic immediate at the given use
    /// position; returns false to let the parser try other interpretations.
    /// </summary>
    public virtual bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        value = 0;
        return false;
    }

    /// <summary>
    /// Called by DialectRegistry.Register exactly once when the dialect is
    /// registered. The implementation stores the assigned ID in its own static
    /// slot — the registry never references a dialect class by name.
    /// </summary>
    internal abstract void OnRegistered(DialectId id);
}
