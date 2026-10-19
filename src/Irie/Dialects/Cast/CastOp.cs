namespace Irie.Dialects.Cast;

public enum CastOp : ushort
{
    /// <summary>
    /// %y : iM = cast.trunc %x : iN     (M &lt; N)
    ///   Truncate a wide integer to a narrower one by discarding the high bits.
    ///   Operand layout: def[0] = result vreg (iM), use[0] = source vreg (iN).
    /// </summary>
    /// <remarks>
    /// The legalizer lowers this to a byte-level slice of the source vreg:
    ///   - If M = 8, it becomes a `pseudo.extract %x, 0` (a single byte).
    ///   - Otherwise it becomes a `pseudo.unmerge` of %x into N/8 byte vregs,
    ///     followed by a `pseudo.merge` of the low M/8 byte vregs into the
    ///     result.
    /// </remarks>
    Trunc,

    /// <summary>
    /// %y : iN = cast.zext %x : iM     (M &lt; N, zero-extend)
    ///   Listed for completeness. Not yet implemented — added
    ///   when a test needs it.
    /// </summary>
    Zext,

    /// <summary>
    /// %y : iN = cast.sext %x : iM     (M &lt; N, sign-extend)
    ///   Listed for completeness. Needs arith.asr (or equivalent) to broadcast
    ///   the top bit; not yet in the arith dialect.
    /// </summary>
    Sext,

    /// <summary>
    /// %i : i16 = cast.ptrtoint %p : ptr
    ///   Reinterpret a pointer's bits as a same-width integer.
    /// </summary>
    PtrToInt,

    /// <summary>
    /// %p : ptr = cast.inttoptr %x : i16
    ///   Reinterpret a same-width integer as a pointer (widen/narrow with
    ///   zext/trunc first).
    /// </summary>
    IntToPtr,
}
