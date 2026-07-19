namespace Irie.Target.MOS6502;

// Target-private FrameSlot.StackId values for the MOS6502 target (the llvm-mos
// `MOSFrameStackId` analogue). The default id (0) means an absolute-memory
// global, accessed indirect-Y; any other id is a target-private placement that
// only the MOS6502 lowering understands.
public static class MOS6502FrameStackIds
{
    // A frame slot promoted to a fixed zero-page address, accessed via direct
    // zp loads/stores. Mirrors llvm-mos's `MosZeroPage`.
    public const int ZeroPage = 1;

    // A frame slot representing a byte the *caller* placed at a fixed offset
    // from the live soft stack pointer (RC0/RC1) before the call. Unlike
    // ZeroPage/absolute placement, the offset is known at mint time (it's a
    // pure function of argument position) — no placement pass involved, and
    // MOS6502FramePlacementPass must not touch these slots.
    public const int CallerStackArg = 2;
}
