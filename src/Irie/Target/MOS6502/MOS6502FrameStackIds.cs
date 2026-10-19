namespace Irie.Target.MOS6502;

/// <summary>
/// Target-private FrameSlot.StackId values for the MOS6502 target (the llvm-mos
/// `MOSFrameStackId` analogue). The default id (0) means an absolute-memory
/// global, accessed indirect-Y; any other id is a target-private placement that
/// only the MOS6502 lowering understands.
/// </summary>
public static class MOS6502FrameStackIds
{
    /// <summary>
    /// A frame slot promoted to a fixed zero-page address, accessed via direct
    /// zp loads/stores. Mirrors llvm-mos's `MosZeroPage`.
    /// </summary>
    public const int ZeroPage = 1;

    /// <summary>
    /// A frame slot representing a byte the *caller* placed at a fixed offset
    /// from the live soft stack pointer (RC0/RC1) before the call. Unlike
    /// ZeroPage/absolute placement, the offset is known at mint time (it's a
    /// pure function of argument position) — no placement pass involved, and
    /// MOS6502ProgramLayoutPass must not touch these slots.
    /// </summary>
    public const int CallerStackArg = 2;

    /// <summary>
    /// A frame slot representing a byte this function places, for a callee to read,
    /// at a fixed offset from the soft stack pointer just before a call. The
    /// function reserves the largest such area in its prologue (the llvm-mos
    /// "reserved call frame"), so the offset is the byte's position in the callee's
    /// stack-argument list and the stack pointer is unchanged around the call.
    /// Like CallerStackArg, placement leaves these slots alone.
    /// </summary>
    public const int OutgoingStackArg = 3;
}
