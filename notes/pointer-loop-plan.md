# Pointer-walking loops: stop copying pointers into $12/$13 and doing 16-bit adds

Status: not started. Written 2026-10-21 while replacing the C# loop behind `cpblk` with a hand-written
`memcpy` libcall (`src/Irie/Target/MOS6502/libcalls.s`). Self-contained.

## Goal

Make the general pointer loop `while (n-- > 0) *d++ = *s++;` cheap, so code that is not a `cpblk` gets
the speed the `memcpy` routine has. Success is the instruction count of the loop below and of the
corelib loops that have the same shape (`ManagedHeap.Alloc` / `LocalAlloc` zeroing, `Unsafe`-style code).
No lit program may grow (see `hello-world-size-plan.md` for the size baseline and how to measure it).

## Baseline

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
static unsafe void Copy(byte[] to, byte[] from, int count)
{
    fixed (byte* t = to) fixed (byte* f = from)
    {
        var d = t; var s = f;
        for (var remaining = (nint)count; remaining > 0; remaining--) { *d = *s; d++; s++; }
    }
}
```

Compiled with `cs_compiler --optimize` and `dnrc --emit Assembly`, the loop body (`.bb5`/`.bb6`) is
**42 instructions per byte**, roughly 130 cycles. Three 16-bit values (`d`, `s`, `remaining`) live in
homes and each is advanced with a multi-byte add or subtract sequence, and each iteration copies both
pointers into `$12/$13` (the scratch pointer pair) just to do one `LDA ($12),Y` and one `STA ($12),Y`.

llvm-mos compiles the C equivalent (`while (n--) *d++ = *s++;`) to about 15 instructions per byte:
the pointers stay in consecutive imaginary registers so `LDA (__rc4),Y` / `STA (__rc2),Y` address them
directly, and each pointer is bumped with `INC lo` / `BNE` / `INC hi` (the high byte only on carry).

## Where the gap comes from

1. **Pointer homes are not addressable.** The indirect-indexed mode needs the pointer in two adjacent
   zero-page bytes; placement assigns the low and high byte separate homes, so every `mem.load` /
   `mem.store` through a pointer first moves both bytes into the scratch pair. Fix: allow a pointer
   value to be placed as an adjacent pair of homes when it is dereferenced in a loop, and use it in
   place (no copy).
2. **`ptr + 1` is a 16-bit add.** An increment by a constant 1 should be `INC lo; BNE skip; INC hi`
   (and `DEC` for the counter), not `CLC; LDA; ADC #1; STA; TXA; ADC #0; ...`.
3. **The loop variable is a full 16-bit count compared against zero.** `remaining > 0` is a signed
   16-bit compare (6 instructions), where a decrement-and-test of an unsigned count is 2.
4. **Induction variables are not recognised.** llvm-mos's `MOSIndexIV` pass rewrites pointer
   induction variables into a `Y` index when the loop is small enough that `Y` can be the induction
   variable (`LDA (ptr),Y` / `STA (ptr),Y` / `INY`), which is the fastest form for blocks up to 256 bytes.

## Plan

Do these in order; each is measurable on its own by the loop above.

1. **Increment and decrement by a constant 1** (item 2). A shape for `add x, 1` / `sub x, 1` on a
   multi-byte value (`INC` / `DEC` of each byte with a branch on the carry chain). Smallest change,
   and it helps every counter and pointer step, not only this loop.
2. **Dereference in place** (item 1). Placement keeps a pointer's two bytes in adjacent homes (the
   `StaticFrameColorer` already stacks homes; it needs a "pair" constraint), and the `mem.load` /
   `mem.store` variants take the pair without the copy to `$12/$13`. Read `MOS6502Variants` and
   `PlacementChecker` first (the rules in `src/Irie/AGENTS.md` about shape ops apply).
3. **Count-down loop test** (item 3). RangeNarrowing already knows value ranges; a loop that counts an
   unsigned or non-negative variable down to zero can test the zero flag instead of comparing.
4. **Index induction variables** (item 4). Port the idea of `MOSIndexIV`: read
   `MOSIndexIV.cpp` in the llvm-mos checkout (`llvm/lib/Target/MOS/`) before designing it, as the repo's
   rules for design questions say; its legality rules (trip count, no wrap, one use of the pointer) are
   the hard part.

## Tests

- A lit test of the loop above in `src/DotNetro.Compiler.Tests/Lit/` with `DIFF: dotnet emulated`
  (behaviour), plus an `--emit asm` `CHECK` listing in `src/Irie.Tests/Lit/CodeGen/MOS6502/` for the
  shape (complete listing, per `AGENTS.md`).
- Beam-placement goldens will change; regenerate with `dotlit --regenerate` and read the diff.

## Not in scope

`cpblk` itself is a `memcpy` call and stays one: a hand-written routine beats any generated loop for
a run-time count. This plan is about the loops that remain.
