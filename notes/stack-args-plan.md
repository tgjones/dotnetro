# Plan: close `basics/stack-args` in the llvm-mos comparison corpus

## Goal

Convert `ext/llvm-mos-reference/basics/stack-args.c` (10 `i16` params, 20 bytes)
from **blocked** to **converted** in `doc/irie/llvm-mos-comparison.md` by giving
Irie's MOS6502 target a genuine calling-convention path for arguments that
overflow the register budget.

Scope, per discussion: **stack-args only**. `memory/struct-return` and
`memory/struct-return-sret` are a different feature (aggregate/sret ABI) and are
explicitly left blocked — the README's grouping of all three under "aggregate /
sret ABI not modelled" is imprecise; stack-args is really a calling-convention
register-budget gap, unrelated to aggregate passing.

Design direction, per discussion: implement a **genuine dynamic, recursion-safe
soft stack pointer** (mirroring llvm-mos's own mechanism), not a static
per-callee buffer.

## Root cause

`MOS6502CallLowering.EnsureRegBudget` (`src/Irie/Target/MOS6502/MOS6502CallLowering.cs:347-352`)
throws `NotSupportedException` once cumulative argument bytes exceed
`ArgRegs.Length` (16: `$a`, `$x`, `RC2..RC15`). `stack_args` needs 20 bytes (10×
`i16`), so the 9th and 10th parameters (`i`, `j`) overflow by 4 bytes.

## What llvm-mos actually does — traced stage-by-stage through `stack-args.txt`

This matters more than it might look: an earlier draft of this plan proposed
hand-emitting concrete MOS6502 opcodes directly inside `AbiLoweringPass` (the
*first* pass in Irie's pipeline, pre-legalization/pre-isel/pre-RA). Tracing
llvm-mos's own `-print-after-all` dump shows that is **not** the stage where it
does this, and the actual staging matters for correctness, not just style.

- **IRTranslator** (`stack-args.txt:269`): generic `G_FRAME_INDEX` + `G_LOAD`
  off `%fixed-stack.N` — no target knowledge at all.
- **Legalizer** (`:339`): still generic — narrows to `G_LOAD_INDIR_IDX`, but
  still addressed via the abstract frame index, not a real pointer.
- **InstructionSelect** (`:534`) — picks concrete, target-specific opcodes, but
  they are still abstract placeholders on **virtual** registers:
  ```
  %155:anyi8, %156:cc, %157:vc = AddrLostk %fixed-stack.3, 0
  %158:anyi8, %159:cc, %160:vc = AddrHistk %fixed-stack.3, 0, %156:cc
  %28:imag16 = REG_SEQUENCE %155:anyi8, %subreg.sublo, %158:anyi8, %subreg.subhi
  ...
  %58:ac, ... = ADCIndirIdx %66:ac(tied-def 0), %28:imag16, %118:yc, %53:cc(tied-def 1)
  ```
  `AddrLostk`/`AddrHistk` ("materialize the low/high byte of this stack slot's
  address") are selected opcodes whose *destinations are still virtual
  registers* — register allocation hasn't run yet.
- **Greedy Register Allocator** (`:1284`) colors those virtual registers
  normally, exactly like any other value.
- **Prologue/Epilogue Insertion** (`:1494`) is where `AddrLostk`/`AddrHistk`
  finally expand into concrete arithmetic against the *real* `$rc0`/`$rc1`:
  ```
  $a = COPY $rc0
  $a, renamable $c, ... = ADCImm $a(tied-def 0), 1, $c(tied-def 1)
  renamable $rc20 = COPY killed $a
  ...
  ```

So llvm-mos keeps stack-argument addressing generic through legalization,
selected-but-still-virtual through instruction selection and register
allocation, and only expands to concrete zero-page arithmetic in PEI —
**after** RA. Two reasons this isn't just a stylistic preference: frame
offsets genuinely aren't final until RA has run (PEI is where LLVM decides the
function's real frame layout), and forcing everything through fixed physical
registers this early removes exactly the scheduling/coalescing freedom RA
exists to provide.

`stack_args` itself makes no calls, so this file gives no direct evidence for
the *caller*-side sequencing (reserving/releasing stack space around a call)
— see "Open question: the caller side" below.

## Irie already has the matching shape — use it, don't reinvent it

Checked `MOS6502InstructionSelector.cs:1067-1260`: when `mem.load.byte_at` /
`mem.store.byte_at` addresses a `FrameSlot`, instruction selection (not
`AbiLoweringPass`) emits `MOS6502Op.FrameLoadByte`/`FrameStoreByte` — abstract,
target-selected, **still on virtual registers**. The concrete addressing
sequence is only emitted later, post-RA, by `FrameAccessLoweringPass` →
`MOS6502FrameLowering.LowerFrameAccess`. That is the exact same two-stage
shape as llvm-mos's `AddrLostk`/`AddrHistk` → PEI. The plan below reuses this
mechanism for stack args instead of inventing a new one.

Bonus finding: the instruction selector's own comment (`:1071-1077`) already
anticipates today's exact conflict:

> "RC0/RC1 are nominally the soft-stack pointer per CC_MOS, but no current
> pass touches them; when frame slots / spilling land in step 11+, either pick
> a different scratch pair or move the soft stack pointer."

This means there are **two**, not one, existing declarations of a
`PointerZpLo`/`PointerZpHi` = `RC(0)/RC(1)` scratch pair that both need to move
before RC0/RC1 can safely become a genuinely live SP:
- `MOS6502FrameLowering.cs:108-109` (the pass that actually emits the
  addressing sequence, post-RA).
- `MOS6502InstructionSelector.cs:1078-1079` (a second, independent copy, used
  when *declaring* the implicit-clobber operands on the `FrameLoadByte`/
  `FrameStoreByte` instructions it creates for ordinary locals).

These must be changed together — they describe the same physical registers
and must stay in sync.

## What already exists vs. what's new (confirmed by reading the code)

- `src/Irie/Mir/FrameSlot.cs`: frame slots ("the unified-MIR pipeline places
  address-taken locals... into named `.bss`-style globals **rather than a real
  call stack**") are purely static, absolute-memory allocations by default
  (`StackId = 0`, the `DefaultStackId` sentinel).
- `src/Irie/Target/MOS6502/MOS6502FramePlacementPass.cs`: promotes frame slots
  of **non-reentrant** functions into a shared, compile-time-colored zero-page
  window (`StaticFrameColorer`), by setting `StackId = ZeroPage` (=1) and a
  computed `Offset` on **every** slot in an eligible function's
  `FrameSlots` list, unconditionally. This is important below: it does not
  currently check whether a slot already carries a non-default placement.
- `src/Irie/Passes/FrameLoweringPass.cs` runs at pipeline position 5, **before**
  `AbiLoweringPass` (position 6) — confirmed from the actual wiring in
  `src/Irie.Tools.Compiler/Program.cs:71-154`. It materializes every slot in
  `function.FrameSlots` **as of when it runs** into a `.bss`-style `MirGlobal`
  and rewrites `mem.frame_addr`. Slots minted later (by `AbiLoweringPass` or,
  today, by `RegisterAllocatorPass`'s spill slots) are invisible to it — it
  already ran. **This means the callee-side design below must not route
  through `mem.frame_addr`/`mem.symbol`/`FrameLoweringPass` at all** — it
  mints its `FrameSlot`s too late in the pipeline for that path. Instead it
  emits the target's already-abstract `FrameLoadByte`/`FrameStoreByte` op
  directly, the same shape the instruction selector would otherwise produce.
  (This sidesteps the issue entirely rather than fixing it — no wasted `.bss`
  byte gets created either, since `FrameLoweringPass` never sees these slots.)
- `pseudo.spill`/`pseudo.reload` (RA's generic memory-spill path) mint
  abstract `FrameSlot`s but have no lowering to asm yet
  (`PseudoExpansionPass.cs:31-37` throws if either survives) — a separate,
  pre-existing gap, not required for `stack_args`, not touched by this plan.
- No program entry-point / startup pass exists anywhere in Irie; nothing
  currently initializes RC0/RC1 to a real address. See "Explicit non-goals."

## Design: Irie's stack-argument ABI extension (callee side — fully specified)

New pieces:

**1. New `FrameSlot.StackId`** — `src/Irie/Target/MOS6502/MOS6502FrameStackIds.cs`:
```csharp
// A frame slot representing a byte the *caller* placed at a fixed offset
// from the live soft stack pointer (RC0/RC1) before the call. Unlike
// ZeroPage/absolute placement, the offset is known at mint time (it's a
// pure function of argument position) — no placement pass involved, and
// MOS6502FramePlacementPass must not touch these slots (see below).
public const int CallerStackArg = 2;
```

**2. `MOS6502CallLowering.LowerFormalArguments`** (`MOS6502CallLowering.cs:51-85`):
once `regIdx >= ArgRegs.Length`, instead of throwing via `EnsureRegBudget`,
each remaining parameter byte at sequential position `k` (0-based, in
argument-declaration order, LSB-first per multi-byte value — Irie's own
internal convention; llvm-mos's own stack layout for this case is
byte-reversed in a way that looks like an artifact of its lowering, not a
load-bearing ABI detail worth replicating bit-for-bit):

```csharp
var slot = new FrameSlot(function.FrameSlots.Count, IRType.I8, $"{function.Name}_stackarg{k}")
{
    StackId = MOS6502FrameStackIds.CallerStackArg,
    Offset = k,
};
function.FrameSlots.Add(slot);

var valueVreg = function.CreateVirtualRegisterInClass(MOS6502RegisterClass.Ac, "ac");
builder.BuildInstruction(
    MOS6502Dialect.OpRef(MOS6502Op.FrameLoadByte),
    new VirtualReg(valueVreg, IsDefinition: true),
    new Symbol(slot.SymbolName),
    new Immediate(0),
    new PhysicalReg(MOS6502Registers.Y, IsDefinition: true, IsImplicit: true));
    // no $rc0/$rc1 clobber — see LowerCallerStackArgAccess below, it only
    // *reads* the live SP, never rebuilds a pointer into it.
```
(then funnel out of `$a`/`Ac` into a flexible `Anyi8` vreg, same idiom
`MOS6502InstructionSelector.SelectMemLoadByteAt` already uses, before feeding
it to the existing multi-byte `BuildMergeInto` reassembly.)

This is target-specific code emitted from within `CallLowering` — deciding
"does this argument overflow the register budget" is inherently a
calling-convention decision, so it's appropriate for it to live here rather
than behind a generic dialect op. It bypasses `mem.frame_addr` entirely (per
the pipeline-ordering finding above) and instead builds the *same*
already-abstract, virtual-register-based op the instruction selector would
otherwise build for a local — so it needs **zero changes** to
`MOS6502InstructionSelector.cs`'s dispatch logic, and flows through
`LegalizerPass`/`InstructionSelectorPass` untouched exactly the way
`mos6502.jsr.abs` already does when `LowerCall` emits it directly today (see
"Precedent" below).

**3. `MOS6502FrameLowering.LowerFrameAccess`** (`MOS6502FrameLowering.cs:123-138`):
add a third branch:

```csharp
if (slot.StackId == MOS6502FrameStackIds.ZeroPage)
    LowerZeroPageAccess(builder, op, slot, offset, valueReg);
else if (slot.StackId == MOS6502FrameStackIds.CallerStackArg)
    LowerCallerStackArgAccess(builder, op, slot, offset, valueReg);
else
    LowerAbsoluteAccess(builder, op, symbol, offset, valueReg);
```

`LowerCallerStackArgAccess` is *simpler* than `LowerAbsoluteAccess` — no
pointer needs building at all, because RC0/RC1 already **is** the right
pointer (this is the payoff of making RC0/RC1 a real, live SP instead of
disposable scratch): just set `$y` to the slot's offset and address off
RC0/RC1 directly.

```csharp
private static void LowerCallerStackArgAccess(
    MirBuilder builder, MOS6502Op op, FrameSlot slot, long offset, int valueReg)
{
    EmitLoadY(builder, slot.Offset + offset); // $y = ldy.imm #N
    if (op == MOS6502Op.FrameLoadByte)
    {
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(MOS6502Op.LdaIndY),
            new PhysicalReg(valueReg, IsDefinition: true),
            new PhysicalReg(MOS6502Registers.RC(0), IsDefinition: false),
            new PhysicalReg(MOS6502Registers.RC(1), IsDefinition: false, IsImplicit: true),
            new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
        return;
    }
    builder.BuildInstruction(
        MOS6502Dialect.OpRef(MOS6502Op.StaIndY),
        new PhysicalReg(MOS6502Registers.RC(0), IsDefinition: false),
        new PhysicalReg(valueReg, IsDefinition: false),
        new PhysicalReg(MOS6502Registers.RC(1), IsDefinition: false, IsImplicit: true),
        new PhysicalReg(MOS6502Registers.Y, IsDefinition: false, IsImplicit: true));
}
```

This reads RC0/RC1 as **uses**, never as defs — correctly modeling that this
access must not disturb the live SP. This is cheaper than llvm-mos's own
output for this case: llvm-mos's post-PEI form materializes a *separate*
16-bit pointer pair (`AddrLostk`/`AddrHistk` + `REG_SEQUENCE`) per stack-arg
byte (`__rc18/19`, `__rc20/21`, ...); Irie needs none of that, since the
offset fits in `$y` directly against the pointer that's already live.

**4. `MOS6502FramePlacementPass.cs:56-96`** — **required correctness fix**:
today it processes *every* slot in an eligible (non-reentrant) function's
`FrameSlots` unconditionally, overwriting `StackId`/`Offset`. A
`CallerStackArg` slot must never be promoted into the zp colouring window —
it doesn't represent this function's own storage at all, it's a read into the
*caller's* dynamically-positioned memory, so "promoting" it would silently
double-book zero-page window space against unrelated live data.

Confirmed this against llvm-mos's own analogous pass,
`MOSZeroPageAlloc.cpp` (`~/Code/llvm-mos/llvm/lib/Target/MOS/MOSZeroPageAlloc.cpp`,
`collectCandidates()` at lines 612-635): it excludes incoming/outgoing
stack-argument frame objects **structurally**, not via a special-case check.
LLVM gives fixed stack objects (stack-passed incoming args, outgoing
call-frame slots) *negative* frame-index numbers — confirmed in the
`stack-args.txt` dump itself (`fi#-4..fi#-1` for the incoming args vs.
`fi#0`/`fi#1` for the function's own locals). `collectCandidates` only
scans `MO.getIndex() >= 0` when tallying usage benefit, and its candidate
loop runs `for (int I = 0, E = MFI.getObjectIndexEnd(); I != E; ++I)` —
starting at `0`, so fixed (negative-index) objects are outside its range
entirely. There's no explicit "is this a caller-owned arg, skip it" branch
anywhere in that pass; the object numbering itself makes it structurally
impossible for a fixed object to be considered.

Irie's `FrameSlot` has no equivalent fixed/regular index-sign distinction, so
the natural equivalent given Irie's own data model is a StackId check: skip
any slot whose `StackId != FrameSlot.DefaultStackId` in both `FrameSize()`
(footprint calculation) and the colouring loop. This reproduces the same
structural exclusion llvm-mos has, just expressed differently. It is a no-op
for every existing test (all current slots are minted with the default
StackId, 0) — it only changes behavior for the new `CallerStackArg` slots
this plan introduces.

**5. Move the scratch pointer off RC0/RC1** — both copies:
- `MOS6502FrameLowering.cs:108-109`: `PointerZpLo`/`PointerZpHi` →
  `RC(30)/RC(31)` (`$1E`/`$1F`).
- `MOS6502InstructionSelector.cs:1078-1079`: the same constants, same change.

  `RC(30)/RC(31)` are already used the same way — as transient,
  never-held-live pointer scratch — by
  `MOS6502CallLowering.CallTargetLo/CallTargetHi` (`:312-313`, the
  indirect-call trampoline), so this reuses an existing "momentary scratch"
  convention rather than inventing a new one, and does **not** shrink the
  general allocatable register pool (`Imag8Regs` stays `RC2..RC31` in
  `MOS6502RegisterInfo.cs` — no existing golden's register *choices* should
  shift, since no register left the allocatable set). It does change the
  addressing-mode *content* of frame accesses that go through
  `LowerAbsoluteAccess`, so goldens that exercise it need regenerating (see
  Test plan).

### Precedent for emitting target opcodes this early being safe

`LowerFormalArguments`/`LowerCall` run inside `AbiLoweringPass`
(`src/Irie/Passes/AbiLoweringPass.cs`), the first pass — before legalization,
selection, or RA. Emitting the concrete-opcode-but-virtual-register
`FrameLoadByte` there (rather than only from within
`MOS6502InstructionSelector`, its only current caller) is new, but not
unprecedented: `LowerCall` already emits `mos6502.jsr.abs` directly today, and
that instruction — a genuine target-dialect opcode with `PhysicalReg` implicit
operands — survives `LegalizerPass` and `InstructionSelectorPass` untouched
("Already-selected target ops pass through", `MOS6502InstructionSelector.cs`).
`FrameLoadByte` here is actually a *smaller* deviation than the jsr precedent:
its main operand (`valueVreg`) is a virtual register, letting RA make normal
placement decisions for it — exactly matching how llvm-mos's `AddrLostk`
destinations stay virtual until RA runs.

One thing to verify empirically once this is implemented, not assumed: that
`LegalizerPass` doesn't choke on seeing an already-target-dialect
`FrameLoadByte` instruction sitting in the function before it runs (this
never happens today, since it's currently only ever created *by*
`InstructionSelectorPass`, which runs after the legalizer). The `jsr.abs`
precedent suggests target-dialect instructions are simply left alone by
`LegalizerPass`, but confirm this holds for `FrameLoadByte` specifically
before relying on it.

## Open question: the caller side

`stack_args` makes no calls, so nothing in this corpus case exercises writing
overflow arguments *into* the soft stack before a `jsr`, or reserving/
releasing the space for them. I don't have direct file evidence for how
llvm-mos sequences that half.

By analogy with the callee-side finding above (frame layout, including
outgoing-call space, isn't finalized until PEI), the likely-correct design is
**not** a per-call-site reserve/write/release sequence hand-built in
`LowerCall`, but something closer to LLVM's standard treatment of outgoing
call-frame space: the *caller function's own* frame size is computed once
(covering the worst-case outgoing-args footprint across all its call sites,
similar to `maxCallFrameSize`), reserved/released once in that function's own
prologue/epilogue (the same place `PrologueEpilogueInsertionPass` already
emits the callee-saved `PHA`/`PLA` sequence), and each individual call site
just writes bytes at fixed offsets into space that's already reserved —
mirroring how `AddrLostk`/`AddrHistk` also stay virtual-register pseudo-ops
until PEI expands them. This would need:
- A new abstract "outgoing stack arg" write op (parallel to
  `FrameLoadByte`/`FrameStoreByte`), minted per call site in `LowerCall`.
- Each caller function tracking its own maximum outgoing-args footprint across
  all call sites (a new piece of per-function state, alongside `FrameSlots`).
- The actual SP reserve/release folded into `PrologueEpilogueInsertionPass`'s
  existing prologue/epilogue emission, sized to that per-function maximum.

This is materially more design work than the callee side, and I'd want either
a corpus case that exercises it or explicit sign-off on the above sketch
before implementing it — **recommend treating it as a separate follow-up**,
scoped and planned on its own, rather than folding it into this change
un-evidenced. The callee side alone is sufficient to convert `stack_args`
itself (it has no calls), so it does not block this plan's actual goal.

## Explicit non-goals / follow-ups

- **The caller-side reserve/write/release mechanism** — see above; deferred to
  a follow-up plan.
- **No program-entry-point / SP-initialization pass is added.** Nothing in
  Irie today drives "compile a whole program and run it" (Irie is not wired
  into the main `DotNetro.Compiler` pipeline per `CLAUDE.md`), and all
  `Irie.Tests` in this area are golden/text-comparison only (confirmed: no
  `Aemula`/emulator usage anywhere under `src/Irie.Tests`) — so nothing
  currently needs RC0/RC1 to hold a real address at runtime. Building an
  unused init hook now would be speculative. **Documented precondition**:
  before any compiled function that reads stack args (or, later, uses
  `pseudo.spill`/`reload`) can execute correctly, something must initialize
  RC0/RC1 to point at a valid, sufficiently large scratch region. Flag this
  again when Irie gains an actual "assemble a whole program" driver.
- **`pseudo.spill`/`pseudo.reload` lowering** remains unimplemented — separate,
  pre-existing gap, not required for `stack_args`.
- **`LowerIndirectCall` stack-arg support** not added — no corpus case needs
  it, and it would need the same (deferred) caller-side mechanism anyway.
- **struct-return / struct-return-sret** remain blocked — different feature.

## Test plan

1. **The corpus conversion** (the actual goal): add
   `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/basics/stack-args.irie`,
   following the existing convention (see `add-i32.irie` for the
   single-function shape) — `; RUN: @iriec --target mos6502 --emit=asm @file`,
   a header naming the corpus case, and a faithful MIR mirror:
   ```
   func @stack_args : (i16, i16, i16, i16, i16, i16, i16, i16, i16, i16) -> i16 {
   bb0(%0 : i16, %1 : i16, %2 : i16, %3 : i16, %4 : i16, %5 : i16, %6 : i16, %7 : i16, %8 : i16, %9 : i16):
       %10 : i16 = arith.addi %0, %1
       %11 : i16 = arith.addi %10, %2
       %12 : i16 = arith.addi %11, %3
       %13 : i16 = arith.addi %12, %4
       %14 : i16 = arith.addi %13, %5
       %15 : i16 = arith.addi %14, %6
       %16 : i16 = arith.addi %15, %7
       %17 : i16 = arith.addi %16, %8
       %18 : i16 = arith.addi %17, %9
       core.return %18
   }
   ```
   Generate the `CHECK` block via `dotlit --regenerate` per house style (do not
   hand-write it). This exercises only the callee side, which is all this
   corpus case needs.

2. **Regenerate the goldens that exercise `LowerAbsoluteAccess`** after the
   `RC(30)/RC(31)` scratch-register change (confirmed via `grep` for
   `$00`/`$01` in their CHECK blocks):
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/AddressTakenFrameSlot-MachineCode.irie`
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/FrameSlotStructRoundtrip-MachineCode.irie`
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/StaticFrameAllocOverflow-MachineCode.irie`
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/StaticFrameAllocRecursive-MachineCode.irie`
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/RuntimePointerLoadStore-MachineCode.irie`
   - `src/Irie.Tests/Lit/CodeGen/MOS6502/ParamPlusConstScratchClobber-MachineCode.irie`

   Diff each by hand before accepting — expect only the `$00`/`$01` →
   `$1E`/`$1F` addresses to change, not the instruction shape. A shape change
   means the register swap broke something.

3. `dotnet test --project src/Irie.Tests` (full suite — targeted lit-test
   filtering isn't available for `.irie` cases per `CLAUDE.md`).

4. Regenerate `doc/irie/llvm-mos-comparison.md`
   (`dotnet run --project src/Irie.Tools.Reference -- generate-report`) and
   confirm `basics/stack-args` moved from blocked to converted, and no *other*
   case's instruction count moved (the register-scratch change is
   addressing-only, not instruction-count-changing, for every other case).

## Task list

1. `MOS6502FrameStackIds.cs`: add `CallerStackArg = 2`.
2. `MOS6502FrameLowering.cs` + `MOS6502InstructionSelector.cs`: move
   `PointerZpLo`/`PointerZpHi` from `RC(0)/RC(1)` to `RC(30)/RC(31)` in both
   places.
3. Regenerate the six affected goldens; hand-verify shape-only diffs.
4. `MOS6502FramePlacementPass.cs`: skip non-default-`StackId` slots in
   `FrameSize()` and the colouring loop.
5. `MOS6502FrameLowering.cs`: add `LowerCallerStackArgAccess` and the dispatch
   branch for `MOS6502FrameStackIds.CallerStackArg`.
6. `MOS6502CallLowering.cs`: extend `LowerFormalArguments` so overflow
   parameter bytes mint a `CallerStackArg` `FrameSlot` and emit `FrameLoadByte`
   directly (bypassing `EnsureRegBudget`'s throw for the overflow portion; the
   register-arg budget check for the first 16 bytes is unchanged).
7. Add `LlvmMosReference/basics/stack-args.irie` (the corpus conversion);
   regenerate its `CHECK` block.
8. Full `dotnet test --project src/Irie.Tests` run.
9. Regenerate `doc/irie/llvm-mos-comparison.md`; confirm `basics/stack-args`
   converted and no unrelated case regressed.
10. Update `src/Irie.Tests/Lit/CodeGen/MOS6502/LlvmMosReference/README.md`'s
    blocked-cases table (`stack-args` moves out of "aggregate / sret ABI not
    modelled"; that row should now read `memory/struct-return,
    memory/struct-return-sret` only) and its Converted table. Note the
    caller-side gap as a new punch-list entry if a follow-up plan for it
    doesn't exist yet.
