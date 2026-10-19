using Irie.Mir;

namespace Irie.Target.MOS6502;

/// <summary>How an edge relates to the block it enters.</summary>
internal enum ExitRole
{
    /// <summary>The target starts from empty registers with every live value in its home.</summary>
    Conventional,

    /// <summary>The target's only incoming edge: it starts from whatever the registers hold.</summary>
    Single,

    /// <summary>
    /// The first edge placed into a join (a block with several incoming edges): what
    /// the registers hold here becomes the state the join starts from.
    /// </summary>
    Primary,

    /// <summary>
    /// Any other edge into a join: it moves values until the state is the one the
    /// join starts from.
    /// </summary>
    Secondary,
}

/// <summary>
/// What a block hands to the block it enters, and what that block then starts from:
/// what A, X, Y, the carry and the scratch pointer pair hold (vreg ids, block
/// parameters, or known bytes), and which values have their home written. A value
/// live into the block is in one of the registers or in its home.
/// </summary>
internal sealed class Snapshot
{
    /// <summary>A register content from here up is a known byte (ImmBase + value).</summary>
    public const int ImmBase = 1 << 20;
    public const int Empty = -1;

    public required int[] R;
    public required int C, PtrLo, PtrHi;
    public required HashSet<int> Homed;
}
