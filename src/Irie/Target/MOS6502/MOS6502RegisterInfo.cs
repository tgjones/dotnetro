using Irie.Mir;

namespace Irie.Target.MOS6502;

public sealed class MOS6502RegisterInfo : TargetRegisterInfo
{
    private static readonly int[] AcRegs    = [MOS6502Registers.A];
    private static readonly int[] XcRegs    = [MOS6502Registers.X];
    private static readonly int[] YcRegs    = [MOS6502Registers.Y];
    private static readonly int[] CcRegs    = [MOS6502Registers.C];
    // RC0/RC1 are reserved as the soft stack pointer per CC_MOS; RC18/RC19 as
    // momentary pointer scratch (MOS6502FrameLowering/MOS6502InstructionSelector's
    // PointerZpLo/Hi). Both pairs are excluded from the allocatable range: unlike an
    // ordinary Imag8 register, these are written and read back within a single,
    // tightly-sequenced expansion with no vreg standing in for the value in
    // between, so RA must never be free to plant an unrelated live value there in
    // the gap.
    //
    // The allocatable zero-page pool has two parts:
    //   * RC2..RC17 — the fixed, caller-saved ABI registers (arguments, results,
    //     scratch). Free to use (they cost no zero-page budget) for any value that
    //     is not live across a call, since a call clobbers them.
    //   * $h0.. — the function's HOMES: an effectively unbounded pool of
    //     function-local zero-page bytes. A call does not clobber a home: program
    //     layout stacks every function's block of homes above the blocks of all its
    //     callees (MOS6502ProgramLayoutPass), so nothing is saved or restored
    //     around a call. The colouring picks the lowest free home, so a function's
    //     footprint is the number of homes it actually needs.
    private static readonly int[] CallerSavedPool = [.. Enumerable.Range(2, 16).Select(MOS6502Registers.RC)];
    private static readonly int[] Imag8Regs =
        [.. CallerSavedPool,
         .. Enumerable.Range(0, MOS6502Registers.HomeCount).Select(MOS6502Registers.Home)];
    // Anyi8: any 8-bit register (A, X, Y, or Imag8). Used for values that can be
    // stored in any 8-bit location but aren't constrained to a specific
    // architectural register.
    //
    // This list defines the *membership* of the flexible class and the DEFAULT
    // (tie-break) preference order. It is deliberately zp-pool-FIRST: that is the
    // right default for a LONG-lived value, which should sit in the abundant
    // zero-page file and leave the three scarce architectural registers ($a/$x/$y)
    // free for the arithmetic chain. The allocator's
    // cost-driven colour selection (GraphColouringAllocator.ColourPreferenceOrder)
    // overrides this order for SHORT-lived values, promoting $y/$x ahead of the zp
    // pool so a brief cross-instruction value lands in a real register the way the
    // llvm-mos references do (e.g. add-i16 parks its low result byte in $y).
    //
    // $y was added to close the "$y gap".
    // It competes with indexed addressing as the index register, but `mem.*`
    // indexed forms do not exist yet; when they do, the instruction
    // selector's explicit `$y` operands already become fixed LiveIntervals
    // segments that interfere with any data vreg parked in $y, so data use of $y
    // cannot collide with an index need. $y is ordered LAST among the real
    // registers so $x (no index conflict) is preferred first.
    private static readonly int[] Anyi8Regs =
        [..Imag8Regs, MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y];

    // Axy: the three real architectural registers, no zero-page pool. The
    // absolute-store source class (see MOS6502RegisterClass.Axy). Order is only a
    // tie-break — coalescing with the stored byte's producer is what actually
    // decides which register it lands in (and hence whether the store refines to
    // STA / STX / STY); $a first so an already-in-$a value needs no AMS rewrite.
    private static readonly int[] AxyRegs =
        [MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y];

    public override ReadOnlySpan<int> GetAllocatableRegisters(int classId)
        => classId switch
        {
            MOS6502RegisterClass.Ac    => AcRegs,
            MOS6502RegisterClass.Xc    => XcRegs,
            MOS6502RegisterClass.Yc    => YcRegs,
            MOS6502RegisterClass.Cc    => CcRegs,
            MOS6502RegisterClass.Imag8 => Imag8Regs,
            MOS6502RegisterClass.Anyi8 => Anyi8Regs,
            MOS6502RegisterClass.Axy   => AxyRegs,
            _ => throw new ArgumentException($"Unknown register class {classId}", nameof(classId)),
        };

    public override bool BelongsToClass(int physReg, int classId)
        => MOS6502RegisterClass.OfPhysicalRegister(physReg) == classId;

    public override int ClassOfPhysicalRegister(int physReg)
        => MOS6502RegisterClass.OfPhysicalRegister(physReg);

    public override string  GetRegisterName(int register)   => MOS6502Registers.NameOf(register);
    public override string? GetRegisterClassName(int id)    => MOS6502RegisterClass.GetName(id);
    public override int?    ParseRegister(string name)      => MOS6502Registers.TryParse(name);
    public override int?    ParseRegisterClass(string name) =>
        MOS6502RegisterClass.TryParse(name, out var id) ? id : null;

    public override int FlexibleI8ClassId => MOS6502RegisterClass.Anyi8;

    // After program layout a home is an ordinary zero-page register at its final
    // address, which may be any RC, not only the allocator's pool.
    public override bool IsRegisterInClass(int physReg, int classId)
        => classId is MOS6502RegisterClass.Imag8 or MOS6502RegisterClass.Anyi8 && physReg >= MOS6502Registers.RC(0)
           || base.IsRegisterInClass(physReg, classId);

    // The general-purpose registers the post-RA RegisterScavengingPass may use
    // as copy scratch, in preference order ($a, then $x, then $y). The scavenger
    // picks the first that is DEAD at the copy's point, so an immediate-into-zp
    // copy emitted while $a holds a live parameter byte gets $x or $y instead —
    // no eviction, where the old RA scratch-clobber model forced $a free. See
    // MOS6502PseudoExpander (the only producer of scratch-needing copies today)
    // and RegisterScavengingPass.
    public override ReadOnlySpan<int> GetScratchGprCandidates() => ScratchGprs;

    private static readonly int[] ScratchGprs =
        [MOS6502Registers.A, MOS6502Registers.X, MOS6502Registers.Y];

    // An $x↔$y copy can use $a (T-transfers) OR any free zero-page slot
    // (`STX $tmp ; LDY $tmp`); $x/$y themselves are the copy's ends. Prefer $a
    // (cheap T-transfers, no memory traffic) then the zp pool — a free zp slot
    // essentially always exists, so $x↔$y never needs to spill.
    private static readonly int[] XyScratch =
        [MOS6502Registers.A, .. CallerSavedPool];

    // Per-shape scratch candidates: $x↔$y admits a zp bounce; every other
    // scratch-needing copy (zp→zp, immediate→zp) needs a GPR.
    public override ReadOnlySpan<int> GetScratchCandidates(MirInstruction scratchCopy)
    {
        if (scratchCopy.Operands is [PhysicalReg dst, PhysicalReg src, _]
            && IsXyPair(dst.Id, src.Id))
            return XyScratch;
        return ScratchGprs;
    }

    // Emergency save slots for the scavenger: the spare zero-page registers.
    // Saving a GPR to zero page (`ST? $zp`) and restoring it (`LD? $zp`) needs no
    // further scratch, so whichever zp slot is dead at the copy's point lets the
    // scavenger free up a GPR even when all three are live.
    public override ReadOnlySpan<int> GetEmergencyScratchSaveSlots() => CallerSavedPool;

    private static bool IsXyPair(int a, int b) =>
        (a == MOS6502Registers.X && b == MOS6502Registers.Y) ||
        (a == MOS6502Registers.Y && b == MOS6502Registers.X);

    // The scarce architectural GPRs to prefer for SHORT-lived flexible values
    // Ordered $x, $y, $a:
    //   * $x / $y first because they are the natural home for a brief value that
    //     must survive across the next arithmetic instruction (the references park
    //     add-i16's relocated low result byte in $y while the high byte is added
    //     in $a). Interference excludes whichever of them is busy, so a value
    //     lands in the free one rather than the zero-page pool.
    //   * $a LAST: arithmetic (ADC/SBC/CMP/EOR …) is pinned to $a on this chip, so
    //     a value parked in $a almost always collides with the next chain link;
    //     keeping $a out of the short-range data preference leaves it free for the
    //     arithmetic it is needed for. ($a is still legal — it is in the class —
    //     it is merely the last GPR tried before the zp pool.)
    // Long-lived values ignore this list and take the class's zp-first default
    // order, keeping all three GPRs free for the arithmetic chain.
    public override ReadOnlySpan<int> GetShortRangeGprPreference() => ShortRangeGprs;

    private static readonly int[] ShortRangeGprs =
        [MOS6502Registers.X, MOS6502Registers.Y, MOS6502Registers.A];

    // No register is callee-saved: values that must survive a call sit in homes,
    // which program layout keeps disjoint from every callee's zero-page use.
    // (Hand-written libcalls still save and restore the RC20..RC31 they use; that
    // only costs them a few bytes and keeps them correct for any layout.)
}
