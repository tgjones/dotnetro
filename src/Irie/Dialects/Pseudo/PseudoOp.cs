namespace Irie.Dialects.Pseudo;

public enum PseudoOp : ushort
{
    /// <summary>
    /// %d = pseudo.copy %s   (or $a = pseudo.copy %s, or any vreg/physreg mix).
    /// The universal copy; pre-isel carries the source vreg's type, isel can
    /// re-use it with a class on one side, RA coalesces/eliminates it, and
    /// PseudoExpansionPass lowers any that survive to target moves.
    /// </summary>
    Copy,

    /// <summary>%w = pseudo.merge %lo, %mid, …  — narrow parts → wide.</summary>
    Merge,

    /// <summary>%lo, %hi = pseudo.unmerge %w   — wide → narrow parts.</summary>
    Unmerge,

    /// <summary>
    /// %byte = pseudo.extract %wide, &lt;bit_offset&gt;
    ///   Extract a sub-range from %wide starting at &lt;bit_offset&gt; bits. The def
    ///   vreg's type determines the width of the extracted sub-range (e.g. i8
    ///   def → 8-bit slice). Modelled on LLVM G_EXTRACT.
    /// </summary>
    Extract,

    /// <summary>
    /// %new = pseudo.insert %wide, %sub, &lt;bit_offset&gt;
    ///   Insert %sub into %wide at &lt;bit_offset&gt; bits, producing a fresh wide
    ///   value. Modelled on LLVM G_INSERT.
    /// </summary>
    Insert,

    /// <summary>
    /// pseudo.return   — void shell of a return after ABI lowering has copied
    /// results into return physregs. Survives until instruction selection
    /// lowers it to the target return op (e.g. mos6502.rts).
    /// </summary>
    Return,
}
