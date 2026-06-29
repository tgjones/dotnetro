# SSA-based register allocator prototype

## Scope

Replace `TwoAddressInstructionPass` + `RegisterAllocatorPass` (and the
`IRIE_COALESCE`-gated `RegisterCoalescerPass` that sits between them) with a
single new `SsaRegisterAllocatorPass`, enabled by `IRIE_SSA_RA=1`.

The new pass receives the same input `TwoAddressInstructionPass` currently
sees — post-`PhiElimination`, post-`InstructionSelector` MIR, each vreg
defined exactly once (true SSA) and annotated with a `ClassedVReg` register
class — and produces the same output `RegisterAllocatorPass` currently
produces: physreg-only MIR with block live-ins recomputed.

For `add-i8` the input is:

```
bb0():
    [liveins: $a, $x]
    %0 : ac = pseudo.copy $a
    %1 : zp = pseudo.copy $x
    %3 : cc = mos6502.clc
    %5 : ac, %6 : cc = mos6502.adc.zp %0(tied-def 0), %1, %3
    $a = pseudo.copy %5
    mos6502.rts implicit $a
```

The initial target is to make this one test produce the correct golden output
with `IRIE_SSA_RA=1` and no regressions on the default path.

## Algorithm

The approach is the Hack / Braun–Hack SSA coloring (references: Hack et al.
2006 "Towards Register Allocation for Programs in SSA-form"; Braun & Hack 2009
"Register Allocation for Programs in SSA Form"). The four steps below match
the outline in the design discussion; details specific to this codebase follow.

**Step 1 — pressure analysis and spilling.**
For each program point, count live vregs per register class. If pressure
exceeds the class size at any point, spill the cheapest value to a wider class
(inserting a `pseudo.copy` into a `zp`-class vreg and redirecting uses past
the pressure peak). Because the SSA interference graph is chordal, excess
pressure can be read directly from the sorted live-range endpoints without a
full graph traversal.

For `add-i8` this step is a no-op: peak pressure is 1 in every class.
Implement the detection but `throw new NotImplementedException()` on the
spilling branch initially; every current corpus test that passes with the
existing RA either never spills or spills to memory (handled later).

**Step 2 — SSA coloring.**
Process vregs in definition order — within a single block, iteration order
of the instruction list; across blocks, a pre-order DFS of the dominator tree.
This is a perfect elimination ordering for the chordal SSA interference graph,
so greedy coloring always succeeds if pressure ≤ k everywhere (guaranteed by
step 1).

For each vreg:
1. Start with allowed colors = `registerInfo.GetAllocatableRegisters(classId)`
   for its `ClassedVReg.ClassId`.
2. Remove any physreg whose live interval (from `LiveIntervalsAnalysis`)
   overlaps this vreg's interval.
3. Remove any color already assigned to an interfering neighbor (another vreg
   whose interval overlaps this one, already colored).
4. If a congruence class has been pre-assigned a color (see tied-operand
   handling below), prefer that color.
5. Otherwise, assign the first remaining color in allocatable order.

Reuse `LiveIntervalsAnalysis` for both the vreg and physreg intervals. Two
intervals interfere using the existing half-open segment overlap test
(`a.Start < b.End && b.Start < a.End`).

**Step 3 — tied-operand congruence classes.**
This is how the SSA RA handles what `TwoAddressInstructionPass` currently does.

Before coloring (after computing intervals but before the dominator-tree walk):

1. Scan every instruction's `DialectInstructionInfo.TiedOperands`.
2. For each tied `(defIdx, useIdx)` pair, union the def vreg and use vreg into
   a **congruence class** (union-find over vreg IDs).
3. If any two members of a congruence class interfere with each other (their
   intervals overlap), the class cannot be contracted — insert a `pseudo.copy`
   from the use vreg to a fresh vreg of the same class immediately before the
   instruction, update the instruction operand to the fresh vreg, and break the
   pair. (This is the exceptional case. For `add-i8` it never fires: %0 and %5
   are both `ac`-class, and %0's interval ends before %5's starts because %0 has
   no uses after the `adc.zp`.)
4. For each surviving congruence class with ≥ 2 members, the allowed colors of
   the class = intersection of the allowed colors of its members. If the
   intersection is empty, the class cannot be contracted — fall back to step 3
   above.

During coloring (step 2), when processing a vreg that belongs to a congruence
class: if the class already has an assigned color (because another member was
colored earlier), use that color directly (skipping the preference/pick logic).

**Step 4 — apply assignment and recompute live-ins.**
Copy `ApplyAssignment` and `RecomputeBlockLiveIns` verbatim from
`RegisterAllocatorPass` — they operate purely on the `vreg → physreg` map and
are independent of how coloring was performed. Call `function.ClearVRegAnnotations()`
after applying.

The existing `CopyEliminationPass` (already downstream in the pipeline)
eliminates the identity copies that result from coalesced physreg assignments
(`$a = pseudo.copy $a` etc.).

## Design constraints and inelegancies

### A. PhiElimination runs before the new pass, not inside it

The pipeline order is `PhiElim → [SSA RA]`, not `[SSA RA including
phi-lowering] → ...`. By the time the new pass runs, block arguments are gone
— replaced by explicit `pseudo.copy` instructions at predecessor edges.

The "ideal" SSA RA would color in phi form and convert each phi to a copy only
when the two sides land in different registers. Doing it after PhiElim means
every phi edge already has a copy, and the coalescer (via CopyEliminationPass)
removes the ones that turned out free. Functionally equivalent, but slightly
more IR traffic.

For `add-i8` (no phis, no loops) this makes no difference. Moving PhiElim
inside the new pass is a future step, motivated by the first loop test that
needs it.

### B. Physreg live-ins from ABI lowering

After ABI lowering the entry block has `[liveins: $a, $x]` and the first
instructions copy those physregs into vregs:

```
%0 : ac = pseudo.copy $a
%1 : zp  = pseudo.copy $x
```

`LiveIntervalsAnalysis` already computes physreg intervals; these appear as
forbidden colors for any vreg whose interval overlaps a physreg interval.
No new machinery is needed here.

The `pseudo.copy $a → %0` is a free coalescing opportunity: since %0 is in
class `ac` (= {$a}) it is colored $a and CopyEliminationPass removes the copy.

The `pseudo.copy $x → %1` is not coalesceable: $x is not in class `zp`. It
survives as a store and PseudoExpansion emits `stx.zp`.

### C. Tight class constraints from InstructionSelector

After isel most operands are pinned to single-register classes (`ac` = {$a},
`cc` = {$c}). The SSA coloring for these values is trivial — only one color is
available, so the allocator rubber-stamps the isel choice. The real freedom (and
the real benefit of SSA coloring) is for `zp`-class and `any8`-class values.

No inelegance; this is simply the shape of the MIR this target produces.

### D. `InsertRelocationCopiesForConstrainedDefs` goes away

The existing `RegisterAllocatorPass` contains a pre-pass that finds constrained
defs (class `ac` → $a) whose value outlives the defining instruction, inserts a
`pseudo.copy` to a flexible vreg right after the def, and redirects later uses.
This exists because the graph-colouring RA has no way to naturally represent
"two `ac`-class values are simultaneously live and can't both be $a."

In the SSA RA, this is handled by step 1 (pressure analysis): two `ac`-class
values simultaneously live = pressure > 1 at $a = a spill is required. The
spilling step inserts the copy at the precise conflict point. The pre-pass and
its slot-numbering machinery disappear entirely.

### E. The `IRIE_COALESCE` gate

When `IRIE_SSA_RA=1`, `TwoAddressInstructionPass`, `RegisterCoalescerPass`
(regardless of `IRIE_COALESCE`), and `RegisterAllocatorPass` are all skipped.
The `IRIE_COALESCE` gate lives inside the `else` branch.

## Pipeline change

In `src/Irie.Tools.Compiler/Program.cs`, replace the block (currently
lines ~92–109):

```csharp
passMgr.AddPass(new TwoAddressInstructionPass());
if (Environment.GetEnvironmentVariable("IRIE_COALESCE") == "1")
    passMgr.AddPass(new RegisterCoalescerPass(target.RegisterInfo));
passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
```

with:

```csharp
if (Environment.GetEnvironmentVariable("IRIE_SSA_RA") == "1")
{
    passMgr.AddPass(new SsaRegisterAllocatorPass(target.RegisterInfo));
}
else
{
    passMgr.AddPass(new TwoAddressInstructionPass());
    if (Environment.GetEnvironmentVariable("IRIE_COALESCE") == "1")
        passMgr.AddPass(new RegisterCoalescerPass(target.RegisterInfo));
    passMgr.AddPass(new RegisterAllocatorPass(target.RegisterInfo));
}
```

## Incremental implementation steps

Each step should leave all existing tests passing on the default path
(`IRIE_SSA_RA` not set).

### Step 0 — gate and skeleton

Add the `if/else` block above. Create
`src/Irie/Passes/SsaRegisterAllocatorPass.cs` as a `MirFunctionPass` that
`throw new NotImplementedException("SSA RA not yet implemented")`. Verify the
gate compiles and the default tests still pass.

### Step 1 — interference and coloring (single-block, no congruence)

Implement the core:
- Call `new LiveIntervalsAnalysis().Compute(function)` to get vreg and physreg
  intervals.
- For each ordered pair of vregs (by ID), check interval overlap to build
  `Dictionary<int, List<int>> neighbors` (adjacency list).
- Process vregs in instruction-definition order (iterate blocks, iterate
  instructions, collect each vreg def in order). For each vreg:
  - allowed = `registerInfo.GetAllocatableRegisters(classId).ToHashSet()`
  - remove physreg conflicts (physreg intervals that overlap this vreg's interval)
  - remove colors taken by already-colored neighbors
  - assign first remaining color; panic if empty (no `add-i8` path reaches this)
- Call `ApplyAssignment` and `RecomputeBlockLiveIns` (copy from
  `RegisterAllocatorPass`) then `function.ClearVRegAnnotations()`.

At this point `IRIE_SSA_RA=1 iriec --target mos6502 --emit=asm add-i8.irie`
should produce the correct output _if_ the congruence step is not yet needed.
For `add-i8` it is not, because both tied vregs are `ac`-class (single member
$a): the coloring assigns both $a without any special handling, satisfying the
tied constraint automatically.

Run `dotnet test --project src/Irie.Tests` (default path) to confirm no
regressions. Run the single `add-i8` test manually under `IRIE_SSA_RA=1` to
confirm the golden.

### Step 2 — tied-operand congruence classes

Implement the pre-coloring scan (step 3 of the algorithm section above):
- Build a union-find over vreg IDs, initialized with each vreg as its own class.
- For each instruction, read `GetInstructionInfo(opcode).TiedOperands` and union
  tied (def, use) pairs.
- For each congruence class with > 1 member, check pairwise interference. If any
  pair interferes, insert a `pseudo.copy` to break the pair and remove it from
  the class. (For correctness: the fresh vreg gets the same `ClassedVReg` as the
  original use vreg; recompute intervals after any insertion.)
- Store the class color alongside the assignment map; during coloring, members of
  the same class read/write the same slot.

This step is a verified no-op for `add-i8` but is needed for tests where the
tied vregs are in a multi-register class.

### Step 3 — multi-block / dominator-tree ordering

Implement pre-order DFS of the dominator tree and process vregs in that order
rather than flat block order. This is required for functions with control flow.

The standard dominator-tree construction for the MIR is a straightforward
iterative post-order algorithm over `MirBlock.Predecessors` / `.Successors`
(already available via `RebuildCfg`). Alternatively, since block 0 is always
the entry and the blocks are in a reasonable order already, a simple reverse-
post-order (RPO) traversal is sufficient as the PEO substitute.

### Step 4 — spilling

When step 1's coloring fails (no color available for a vreg), implement the
spilling step:
- Compute per-class pressure at each program point (from live-interval
  endpoints).
- For each class where pressure > |class|, find the vreg with the longest span
  and insert a `pseudo.copy` to a wider-class fresh vreg at the earliest point
  where pressure is exceeded. Redirect uses past that point.
- Recompute intervals and retry coloring.

This mirrors the pressure-splitting approach in the Braun & Hack paper and is
a strict simplification of the existing `SpillVregs` / `TrySplitToRegister`
machinery.

## Expected output for `add-i8` under `IRIE_SSA_RA=1`

After steps 1–3, the assignment is:
- `%0 : ac` → `$a`
- `%1 : zp` → `$zp2` (first available zp register, RC2 = address $02)
- `%3 : cc` → `$c`
- `%5 : ac` → `$a` (no interference with %0: %0's interval ends before %5's
  starts, because %0 has no uses after `adc.zp`)
- `%6 : cc` → `$c` (dead immediately)

After `ApplyAssignment` and downstream `CopyEliminationPass` + `PseudoExpansion`:

```
add_i8:
    STX $02
    CLC
    ADC $02
    RTS
```

Matches `ext/llvm-mos-reference/basics/add-i8.s` exactly.
