# Register allocation for the 6502 — design

Irie has no register allocator in the usual sense (no register classes, no
interference graph over K registers, no spiller). On the 6502 the usual problem does
not exist, and a different one takes its place. This document explains what that
problem is, how Irie solves it, and why the design looks the way it does.

**Goal:** the best code we can get, measured in bytes and in cycles (frequency
weighted). Compile speed matters, but comes second.

Where the code lives: the pass pipeline is `PipelineBuilder.AddStandardPipeline`
(`src/Irie/Passes/PipelineBuilder.cs`); the 6502 pieces are in `src/Irie/Target/MOS6502/`
(overview in its [README](../../src/Irie/Target/MOS6502/README.md); the class comments of
`MOS6502BeamPlacementPass`, `MOS6502Placer`, `MOS6502Variants`, `PlacementChecker`,
`MOS6502HomeColouring` and `MOS6502ProgramLayoutPass` hold the details). Measured
comparisons with llvm-mos and Oscar64 are in [llvm-mos-comparison.md](llvm-mos-comparison.md).

---

## 1. What the problem is on the 6502

On a modern CPU the hard question is *fit*: K registers, more live values than K, so
who goes to memory. Once zero page is treated as registers, the 6502 has the opposite
problem:

- **Within one function, fit is free.** There is always another zero-page byte, so
  spilling inside a function almost never happens.
- **Zero page is scarce across the whole program.** A DotNetro program never returns
  to BASIC, so it owns the language workspace: `$00–$8F` on the BBC Micro and
  `$02–$8F` on the C64, about 140 bytes. (The C64 figure rests on the same rule: the
  KERNAL uses `$90–$FF`, and BASIC's use of `$02–$8F` stops mattering once the
  program never returns.) That budget is shared by every function, so zero-page
  layout is a *whole-program* problem, not a per-function one.
- **A, X and Y are not interchangeable registers.** Each is a different set of
  instructions:
  - **A** is the only accumulator; every ALU operation reads and writes it.
  - **X and Y** can count (`INX`/`DEY`), compare (`CPX`/`CPY`) and index
    (`abs,X`, `abs,Y`). Only **Y** gives `(zp),Y`, and there is no X↔Y transfer.
  - **Zero page** can be shifted, rotated, incremented and decremented directly
    (`ASL zp`, `INC zp`).
- **Flags are values.** C carries between the bytes of a multi-byte value; N and Z
  are set as a side effect of almost every load, which can make a compare
  unnecessary.

So register allocation here does not decide *whether* a value fits. It decides three
things:

1. **Which values ever reach memory**, and which live only in A, X, Y or a flag.
2. **For each instruction, which form to use and where its operands are:** is the
   counter `INX` or `INC zp`? Is the compare `CMP` or `CPY`? Is the shift `ASL A` or
   `ASL zp`? Is the load `LDA abs,X` or `LDA (zp),Y`? This is instruction selection
   and placement as **one problem**.
3. **Where each memory-resident value lives:** zero page or absolute memory, and at
   which address, chosen across the whole program.

An allocator built for the modern problem (graph colouring or greedy over register
classes) then needs register classes, hints, relocation copies and post-pass rewrites
to patch the 6502 problem back in. Irie's first allocator was that design; the
evidence below is why it was replaced.

## 2. Evidence behind the design

### 2.1 Where the gap to llvm-mos came from

Comparing the colouring allocator with a *backend-only* llvm-mos baseline (middle-end
disabled, so the same MIR shape), each excess instruction in 12 corpus cases was
classified by cause (manual, ±2 instructions per case):

| Cause | Share of excess |
|---|--:|
| Allocation: placement, coalescing, spills, phi copies | 33% |
| Selection and allocation together: the op exists on several locations but isel pins one (shift A/zp, INC on X/Y/zp, CPX/CPY, index in X/Y) | 23% |
| Other instruction selection (signed compare, constant folding into `#imm`, known-zero bytes) | 24% |
| Legalizer (i16 div/rem through the i32 helper) | 11% |
| Peephole / combine | 7% |
| Block layout | 2% |

The first two rows, 56% of the excess, are what a placement design addresses. C#-shaped
code was worse: on C# kernels the colouring allocator emitted 1.59× the baseline, 47%
of the gap sitting in blocks that held only phi copies and a jump.

The recurring failures of the colouring design:

| Pattern | What happened |
|---|---|
| Arguments evicted | Incoming zp arguments were moved elsewhere so their registers could hold temporaries; a pure permutation of 12 instructions in one case. |
| Phi webs not coalesced | Phi copies duplicated on every split edge, rotating between slots each iteration. |
| Everything goes through A | Every ALU op pinned to A: a 16-bit shift cost 17 instructions where 8 would do (`ASL zp`/`ROL zp`). |
| Loop counters in zp | 16-bit add chains (7 instructions instead of `INY / BNE / INX`). |
| Fixed pointer pair | Every `(zp),Y` copied its pointer into a fixed pair first. |
| Poor spill handling | Reloads of values that never moved; reloads not folded into the `ADC` operand. |

Around a dozen passes existed only to cooperate with or repair that allocator
(relocation copies, two-address fixing, split-every-edge phi elimination,
rematerialisable sinking, parallel-copy sequencing, increment strength reduction, an
addressing-mode selector whose `cmp → cpx/cpy` path never fired because the allocator
had no cost model, scavenging). The cause was structural: the allocator had no cost
model and could not see instruction forms.

### 2.2 Prior art

| Compiler | Approach |
|---|---|
| **Oscar64** | Zero-page temps coloured by interference, with an interprocedural zp layout. A is never allocated. About 130 passes over the generated 6502 code recover A/X/Y reuse (value tracking over A/X/Y and every zp byte; X/Y promotion by greedy scoring), and zp is re-coloured byte by byte afterwards. Aggressive integer range narrowing. Fastest and smallest on almost every C-Bench-64 benchmark. |
| **SDCC** | Optimal (bounded) DP over a tree decomposition of the CFG. Bytewise variables. Cost = a dry run of the real code generator. A/X/Y only; zp is plain static memory. Small code, 2–6× slower than Oscar64. |
| **KickC** | Equivalence classes; exhaustive "uplift" over A/X/Y/zp combinations, each scored by full code generation. Excellent on small programs; doesn't scale. |
| **llvm-mos** | Greedy plus hints in practice. Its planned design (conventional SSA, zp "imaginary" homes coloured first, SDCC-style DP for hardware placement) is mostly stubs. Whole-program static stack and zp promotion by benefit. |
| **cc65** | Soft stack plus peepholes. The floor. |

Conclusions that shaped the design:

1. **Memory placement across the whole program matters at least as much as A/X/Y
   choice.** Oscar64 wins with a greedy A/X/Y story and an excellent memory story;
   SDCC has optimal A/X/Y and a poor memory story.
2. **Cost should come from the code generator itself**, not from hints. llvm-mos's hint
   tables and its `MOSInsertCopies` "to paper over coalescing" are what happens
   otherwise.
3. **Choose the instruction form together with the placement.** llvm-mos fixes
   `CMP GPR,Imag8` before allocation and pays `STY tmp; CPX tmp` in loops.
4. **Allocate per byte.** .NET widens everything to i32; dead or constant high bytes
   must cost nothing.
5. **Decide up front what Oscar64 recovers afterwards.** Its 130 passes are "allocate
   pessimistically, then win it back". A search over register contents that decides
   the same things up front reaches comparable quality with far less machinery, as
   long as the cost model is honest.

---

## 3. The design

### 3.1 Overview

```
legalized byte-level SSA MIR
  → shape selection          (picks operations, keeps fusions; no register choice)
  → home classes             (coalesce phi webs / copies into shared memory homes)
  → PLACEMENT                (per function, callee first: what A, X, Y, C, N/Z hold and which
                              variant each operation uses at every point; stores, reloads, phi moves)
  → checking                 (PlacementChecker, independent of the search)
  → post-placement passes    (branch folding, copy elimination, recursion saves)
  → program layout           (home slot colouring; zp vs absolute; call-graph stacking)
  → pseudo expansion, late optimisation, scavenging, block placement, emission
```

| Piece | Decides | Kind of problem |
|---|---|---|
| Shape selection | which target *operation* implements each MIR op | pattern matching |
| Home classes | which values may share one memory home | interference, no K |
| **Placement** | what A, X, Y, C and N/Z hold at each point; which variant each operation uses; when each value is stored and reloaded | cost-minimising search |
| Home slots | which function-local byte each home uses | interval colouring |
| Program layout | which slots go to zp, which to absolute memory, at what address | whole-program budget plus call-graph stacking |

Functions are placed **callee first** (ascending SCC order of the call graph), so a
caller sees each callee's convention and footprint. DotNetro compiles a closed world,
so the call graph is known; virtual and delegate targets are the address-taken
functions the translator builds vtables for.

### 3.2 Operations with location variants

Shape selection (`MOS6502ShapeSelector`) turns legalized generic MIR into **unpinned
byte operations** on typed vregs: `adc`, `sbc`, `and`, `ora`, `eor`, shifts, `inc`,
`dec`, `cmp`, the fused `cmp_br` (a compare of a byte pair and the exits taken on its
flags, so a wide compare is one `cmp_br` per byte), `load`, `store`, `const`. It
commits to an *operation*, not to an opcode or a register class.

Each operation has a table of **variants** (`MOS6502Variants`), and placement picks
one. A variant says where each operand must be (`A`, `X`, `Y`, `Carry`, `Home`, `Imm`,
`Abs`, `Pair`), where each result appears, the instruction template, its bytes and
cycles, what it clobbers, and what N/Z and C hold afterwards. Examples (cost =
bytes / cycles):

| Operation | Variant | Template | Cost |
|---|---|---|---|
| `add8` | a∈A, b∈home / imm / abs | `ADC zp` / `ADC #` / `ADC abs` | 2/3, 2/2, 3/4 |
| | commuted: b∈A, a∈home | `ADC zp` | 2/3 |
| `inc8` (carry-in known 0, carry-out dead) | a∈X / Y | `INX` / `INY` | 1/2 |
| | a∈A | `CLC; ADC #1` | 3/4 |
| `shl8` | a∈A | `ASL A` | 1/2 |
| `cmp8-branch` | a∈A / X / Y | `CMP` / `CPX` / `CPY` + `Bcc` | |
| `load8 [p + k]` | p∈zp pair, k in Y | `LDA (zp),Y` | 2/5 |
| `store8 v → home` | v∈A / X / Y | `STA` / `STX` / `STY zp` | 2/3 |

**One table is the single source of truth.** Emission instantiates the template, and
bytes and cycles are derived from it (bytes from the opcode tables, cycles from
`MOS6502InstructionDescription.Cycles`, NMOS base cycles; page crossings and taken
branches are not counted). The search therefore cannot disagree with what is emitted,
and a test pins the table's bytes to the encoder's operand sizes. This is SDCC's "dry
run" idea without running a code generator inside the search.

The table holds more variants than a given placement uses (`Abs` forms, compares in
X/Y), so the cost model is complete before a rule needs it.

### 3.3 Locations and state

The allocation unit is the **byte**: the legalizer splits values into i8 pieces. The
other kinds of value are carry bits (i1), pointers (two bytes that must be an adjacent
pair whenever a `(zp),Y` variant uses them), and constants and symbol addresses, which
are free to rematerialise.

At every program point placement tracks a **state**:

```
A, X, Y ∈ { a live value, a known constant, ⊥ }
C       ∈ { a live i1 value, known 0/1, ⊥ }
NZ      ∈ { "reflects value v", ⊥ }
pointer pair  ∈ { the pointer it currently holds, ⊥ }
H       ⊆ live values whose memory home holds the current value (monotone: SSA values never change)
invariant: every live non-constant value is in A, X, Y, C or H
```

This is the value tracking Oscar64 does after the fact, but here it *drives*
decisions instead of repairing them. It subsumes what separate passes used to do: the
`cmp #0` removal (the NZ field), the `LDA #imm` → `TAX`/`INX` rewrite (known
constants), and scavenging of free registers (visible in the state). A second access
through the same pointer sets nothing up, because the pointer pair is tracked.

### 3.4 Placement within a stretch: a beam dynamic program

`MOS6502BeamPlacementPass` walks the operations between two **barriers** (calls, frame
accesses, the hand-written runtime, which are placed deterministically) in order,
keeping the best K states (default 8), each with its accumulated cost and a
back-pointer. For each operation it tries every variant: the state is made to match
the variant by a fixed sequence of moves (save what the variant overwrites, put the
operands in place), the variant is applied, and

```
cost += bytes × w_size + cycles × w_speed × freq(block)
```

is charged. States that agree on every location are merged; the rest are pruned to K.

Two rules keep this simple and always solvable:

- **A store is just a move.** A value is stored to its home lazily, when something is
  about to overwrite its only copy; the save is either a store or a transfer to a free
  register (`TAY`), and both are tried. A value that lives only in registers needs no
  home at all. There is no separate spiller.
- **"Everything in its home, registers empty" is always reachable.** Every operation
  has a variant that runs from that state using only A. So a solution always exists,
  and K = 1 degenerates to a correct greedy baseline.

Other mechanisms the search relies on:

- **Arguments stay where they arrive.** An argument in a zero-page register is its own
  home until a call or a write to that register happens while it is live (decided per
  register). A run of copies setting up a call or a return counts as one parallel move.
- **Values are built in place.** A value whose only use is a copy into a fixed register
  (return values, call arguments) lives in that register from the start.
- **A byte loaded from a global is read in place** (`ADC counter`) when nothing writes
  memory before its last use.
- **A copy between values of one home is a rename.**

### 3.5 Placement across blocks

Phi copies do not exist as IR. A block parameter is a value defined at the block's
entry; getting arguments into it is part of reconciling the predecessor's exit state
with the successor's entry state.

Blocks are placed in reverse post-order, so a predecessor is placed first.

- **Single-predecessor blocks inherit.** They start from the registers (A, X, Y, the
  carry, the pointer pair) and stored-values their predecessor left on that edge.
  The branch is placed both ways, leaving values in registers and storing them first,
  and states that handed over different things are kept apart in the beam.
- **Joins and loop headers start from a snapshot** of the first edge placed into them
  (the *primary*; for a loop header, the edge from outside): what A, X, Y hold, by
  name, and which values are in their homes. Everything not in a register is in its
  home. Every other edge is *secondary* and reconciles to that state: store what the
  join expects in homes, move the parameters, load the expected registers. On a
  `cmp_br` exit the code goes in a new block on that edge, only when non-empty; on a
  `cf.br` it goes before the jump. Edge moves that overwrite each other's sources are
  ordered (read every home before it is written); only a true cycle needs a
  temporary.
- **The primary edge is placed several ways** (registers as they are; everything
  stored; block parameters loaded into free registers in either order, with or without
  the live-in values stored; only the live-in values stored). A candidate state is
  judged by the cost of the join's first stretch and of the blocks after it, down to
  the latch (`Lookahead`, 3 levels); the back edge pays to reach the state being
  judged, so a counter kept in X is judged by what the back edge costs. This stands in
  for placing a loop body once per candidate header state.
- **Lookahead for exits too:** a candidate end state is charged for the first stretch
  of each inheriting successor, searched from the registers it leaves, so a block's
  exit state is chosen for what comes next.
- **Frequency** comes from loop depth (`LoopAnalysis`: natural loops, depth, static
  `base^depth` with base 8).

N/Z is not carried across edges: the late optimisation that drops `cmp #0` after a
load works within one block, so a state claiming inherited N/Z would cost compares the
code still emits. The pointer pair is not carried into joins either.

### 3.6 Home classes and home slots

**Home classes are formed before placement, by coalescing as aggressively as
possible.** With no register limit K, coalescing two values that do not interfere can
never cause a spill; its only cost is zero-page footprint, which the layout step
manages. `HomeClasses` joins every block parameter with the arguments it receives, and
every vreg-to-vreg copy with its source, when the two are never live together.
Interference is by value (a copy does not make source and result interfere); candidates
are tried in decreasing block frequency. Moving between two values of one class is
free, because they share an address, and an edge move whose argument already sits in
the parameter's home is dropped. This replaces George/Briggs conservatism, which
existed only because K was finite, and fixes the argument eviction and rotating phi
slots of §2.1.

Values that live in a zero-page argument register or are rebuilt from a symbol keep
their own home. *Unrelated* values are merged first-fit only in large functions (past
32 homes): below that, sharing only creates edge-move hazards.

**Home slots are assigned after placement**, in `MOS6502HomeColouring`, which runs
first in `MOS6502ProgramLayoutPass` on the final code (after placement, branch folding
and the recursion saves, so the late passes' changes are in). Liveness is of the home
registers themselves (`MOS6502PhysicalLiveness`): a home interferes with every home
live after an instruction that writes it. Colouring is greedy, per byte, most-accessed
home first so hot homes get the lowest colours. Doing this on the code rather than on
the placer's intervals avoids edge-move hazards and reclaims bytes the later passes
made dead.

### 3.7 Program layout: zero page as a whole-program resource

`MOS6502ProgramLayoutPass` (with `StaticFrameColorer`, `CallGraph` and
`ReentrancyAnalysis`) turns each function's coloured homes, plus its zero-page frame
slots, into addresses in one budget. The fixed ABI window (RC0–RC19) sits at the
bottom; layout owns `$14–$8F` (C64 `$16–$8F`), less the two bytes of soft-stack pointer.

1. **No register is callee-saved.** There is no prologue/epilogue save of anything for
   non-recursive code. Instead, call-graph stacking: a function's block of homes is
   placed above the blocks of all its callees. Independent call subtrees overlap, so
   the budget used is the *deepest call path*, not the sum over all functions.
2. **Zero page versus absolute memory is chosen by benefit.** Every variant that names
   a home has an absolute twin (`LDA abs`, `INC abs`), so demoting a home never
   invalidates placement; it changes the address, at +1 byte and +1 cycle per access.
   Homes that do not fit the zero-page budget become `<function>_h<n>` globals, and
   zero page goes to the homes accessed most (weighted by loop frequency). When a call
   path overflows, the callee with the most zero-page homes gives them up first.
3. **Recursion.** Functions in one strongly connected component share one block.
   `MOS6502RecursionSavePass` saves the live homes with `PHA`/`PLA` around calls that
   stay inside the component. Address-taken locals of recursive functions keep the soft
   stack (RC0/RC1).
4. **Indirect calls** (vtable and delegate calls via the trampoline) reach only
   *address-taken* functions, and `CallGraph` knows a call of the trampoline is an
   indirect call. Dialects declare which ops are calls (`Dialect.IsCall`); treating
   any side-effecting instruction with a symbol operand as a call once made
   `lda.imm.symlo @f` (building a function pointer) look like a call of `f`.

The budget is not binding on any program in the test suite (the largest needs about
half the window), which is why two refinements from the original design are not built:
splitting homes into "live across a call" and "not" (the former would sit above the
callees' frames, the latter could overlap them), and keeping `(zp),Y` pointers as
adjacent-pair homes (placement instead sets up the scratch pointer pair before each
access, and tracks it). Both become worth doing if zero page ever binds.

**Interrupts.** No DotNetro program installs an interrupt handler; none is needed for
input (the BBC MOS and C64 KERNAL scan the keyboard in their own interrupts; BBC
analogue joysticks go through `OSBYTE &80`, C64 joysticks are plain loads from CIA 1).
When user handlers arrive (C64 raster interrupts, BBC vsync/timer events), layout has
a place for them: a handler is a separate call-graph root that can preempt anything, so
its slots go above every other frame; a function reachable from both the main program
and a handler is treated as reentrant (cloned for the handler, or given a soft-stack
frame); and the handler saves the A/X/Y it uses.

### 3.8 Per-function calling conventions

Because the world is closed and placement runs callee first, a callee *could* choose
its own convention. The mechanism exists but is **off by default**
(`--choose-conventions`), because the measured gain is under 0.1%.

- `AbiLoweringPass` always emits the default convention (CC_MOS), so every earlier
  pass is unchanged. A `FunctionConvention` (one register per argument and result
  byte, kept on `MirFunction.Convention`) is applied afterwards by renaming the ABI
  copies (`MOS6502ConventionRewriter`): at the callee's entry and returns and, in every
  caller, around the `jsr`. It can name A, X and the argument zero-page registers; bytes
  on the stack and Y are not moved. `Verify` checks that no function reads an argument
  register outside its convention and every `jsr` passes exactly the callee's registers.
- A function gets a choice only if it has a direct caller, is not address-taken, is not
  the first function, is not in a call cycle, and the shapes of its ABI copies are ones
  the rewriter handles. Recursion, indirect-call targets, `runtime.irie`, `InternalCall`
  externs, OS calls (`bbcmicro_os`, `c64_kernal`) and the entry point keep CC_MOS.
- The function is placed under its current convention and under each reordering of its
  A/X argument bytes, then of its A/X result bytes; the cheapest wins. The placement
  cannot see the calls, so every moved byte is charged a transfer (3 bytes, 2 cycles)
  for every call in the module. Without that charge, a callee-only choice swapped the
  arguments of a function with eight call sites and cost every site a swap.
  `SaveState`/`RestoreState` on `MirFunction` undo a trial placement.

Why it gains so little: the zero-page arguments were already free. A caller writes a
callee's argument registers directly and the callee keeps them as its own homes (the
mechanism Oscar64 also uses), so the only remaining freedom is A and X, and moving a
byte between them rarely saves more than the transfer it costs a caller. Letting an
A/X byte move to a free zero-page register as well saved 14 bytes over the whole C#
set for about twice the compile time, and was not kept.

### 3.9 Flags

- **C** is a location. Carry chains, `ASL`→`BCS` bit tests (crc8), and booleans left
  in C for a branch are choices the search can make. A carry the next operation
  consumes stays in C; any other is stored as 0/1 and reloaded with `LDA ; LSR`.
- **NZ** is a "reflects value v" fact. A compare against zero costs nothing when the
  state already reflects v.
- A compare and its branch need not be adjacent: the state models N/Z explicitly.
- Every op that writes N/Z must say so in its description (`INX`/`INY`/`DEX`/`DEY`,
  `INC`/`DEC zp` and the address-byte loads once did not, and the late optimisation
  dropped a `cmp #0` it needed).

### 3.10 Verification

`PlacementChecker` is the safety net, and it is independent of the search. It runs
every block symbolically twice, as shape MIR and as the emitted code, and compares the
stores, calls, branches (with the flags they test), the return registers and, on every
exit, the home of every value live into the successor. A block that starts from a
snapshot is checked from the snapshot, and every edge into it is checked to deliver it.
It runs on every compile (`IRIE_NO_PLACEMENT_CHECK` turns it off; the cost is below
the noise of a compile) and catches a wrong placement as a compile error rather than
wrong code. During development it caught every fault the tests found, including a
save-by-move that clobbered a pointer offset in Y, a flag overwritten between a load
and its branch, and an edge move that read the wrong home.

A shape op the checker does not model silently skips its block, and an emitted op it
does not model is a failure; so adding a variant or a shape op means extending the
checker. Beyond that: every `.cs` lit test runs on the emulator against the host .NET
runtime, and the corpus `.irie` cases execute under `irie-exec`, comparing results and
all globals with the reference interpreter.

### 3.11 What stayed from the old pipeline

`RegisterScavengingPass` (pseudo expansion still mints scratch vregs for copies the
search does not see, e.g. immediate→zp or X↔Y), `CopyEliminationPass`,
`CopyPropagationPass` and `MOS6502LateOptimizationPass` (the `cmp #0` fold, working
within one block). `StaticFrameColorer` was extended into program layout. Everything
specific to graph colouring is gone: relocation copies, two-address fixing, phi
elimination, operand legalisation, parallel-copy sequencing, the addressing-mode
selector, increment strength reduction, the pinning instruction selector, and the
fixed `RC18/19` pointer scratch with its leaky selector cache.

---

## 4. Tuning and what the measurements say

Knobs (`iriec`, `irie-exec`, `dnrc`): `--beam-width N`, `--tune
beam=N,size-weight=N,speed-weight=N,lookahead=N,freq-base=N,choose-conventions`, and the
presets `--optimize-size` (size 8, speed 1) and `--optimize-speed` (size 1, speed 8).
`--beam-width 0` keeps every state: the **oracle** (states that agree on every
location are still merged, and a step holding more than `OracleStateCap` = 20 000 keeps
the cheapest; `--stats` reports `placement.capped-steps`). `--stats` also prints the
homes placed, slots after colouring, zero-page bytes and deepest call path.
`src/Irie.Tools.Reference/sweep.py` measures settings across the corpus and the C# set.

Findings, which are why the defaults are what they are:

- **Beam 8 is within about 0.25% of the oracle** in bytes of the C# set (0.15% on the
  corpus) and 0.13% in cycles; almost no step reaches the oracle's cap, so the oracle is
  exact nearly everywhere. Beam 32 is within 0.01% for 11% more compile time, not a
  clear enough win to change the default. Beam 1 (greedy) is the fast path.
- **Size and speed weights, and the frequency base, do nothing measurable** (under
  0.003% over weights 1–8 and bases 4–16): where a variant is smaller it is almost
  always as fast. The presets exist but have almost nothing to choose between.
- **Lookahead beyond 1 changes nothing**; 3 is kept with margin. Lookahead 0 is not a
  usable setting.
- **Block joins are not where the loss is.** Widening the one join knob (lookahead)
  changes nothing, and the loss to the oracle within a stretch is small. That bounds
  what the join heuristic loses along the dimensions that can be varied, not what an
  exact method over the whole CFG would find; nothing here argues for a tree-decomposition
  DP, so it is not built.
- **Callee-chosen conventions** gain 0.07% of C# bytes for about 15–18% more compile
  time (§3.8).

Rough sizes, for orientation: the beam placement produces about 8% fewer bytes than the
graph-colouring allocator it replaced on the corpus and about 12% fewer on the C#
programs; a naive everything-homed baseline is about 40% larger than the colouring
allocator. Current numbers against llvm-mos and Oscar64 are in
[llvm-mos-comparison.md](llvm-mos-comparison.md).

## 5. Limits and open work

- **The cost model is everything.** If the variant table is incomplete, the search
  confidently picks the wrong thing. A single table driving both cost and emission,
  the oracle and the scoreboard are the defences.
- **Known failure under non-default settings.** With beam 1 or 4, or lookahead 0, the
  checker rejects a few programs (`fibonacci`, `gcd`): a back edge hands over a register
  the loop header does not expect (the handling of registers a loop header inherits,
  `FindInheriting`). It is a compile error, not wrong code; the defaults are unaffected.
- **Not searched:** choosing a join's state by transfers (a return block whose state is
  taken from its first edge makes other arms pay a `TAY`/`TXA` swap); N/Z and the
  pointer pair across edges; Y as an argument or result register; the memory-destination
  forms (`INC zp`, `ASL zp`) only pay once a result can share its operand's home;
  `abs,X`/`abs,Y` loads and stores need the selector to fold an index into the address.
- **Conventions** optimise the callee's view only; costing the calls with the callers'
  own placement is the obvious next step, but the total gain available is under 0.1%.
- **Compile time** matters for the WebAssembly playground; the beam width is the knob,
  and beam 1 is fast.

### Work outside the allocator that still limits quality

No allocator closes these, and on C# some are larger than anything above:

- **Range / demanded-bytes narrowing of i32 to i8/i16.** Oscar64's biggest structural
  advantage. Narrowed bytes vanish from both zp and the instruction stream.
- **Known-zero bytes.** C# output contains dozens of `AND #$00`.
- **Signed compare** without a materialised `EOR #$80` temporary.
- **Middle-end loop work:** loop-invariant code motion (`a.Length` is reloaded every
  iteration), induction-variable narrowing, count-down loops.
- **An i16 div/rem helper.**

## References

- Irie: [MOS6502BeamPlacementPass.cs](../../src/Irie/Target/MOS6502/MOS6502BeamPlacementPass.cs),
  [MOS6502Variants.cs](../../src/Irie/Target/MOS6502/MOS6502Variants.cs),
  [PlacementChecker.cs](../../src/Irie/Target/MOS6502/PlacementChecker.cs),
  [MOS6502ProgramLayoutPass.cs](../../src/Irie/Target/MOS6502/MOS6502ProgramLayoutPass.cs),
  [StaticFrameColorer.cs](../../src/Irie/Passes/StaticFrameColorer.cs),
  [ReentrancyAnalysis.cs](../../src/Irie/Passes/Analyses/ReentrancyAnalysis.cs).
- llvm-mos: `MOSConventionalSSA.cpp`, `MOSImagRegAlloc.cpp` and `MOSRegAlloc.cpp`
  (stub headers describing the planned design), `MOSZeroPageAlloc.cpp`,
  `MOSStaticStackAlloc.cpp`, `MOSRegisterInfo.cpp` (`copyCost`, hints).
- Oscar64 (github.com/drmortalwombat/oscar64): `InterCode.cpp` (`ReduceTemporaries`,
  `MapCallerSavedTemps`, `CheckStaticStack`), `GlobalAnalyzer.cpp` (`CheckFastcall`,
  `AutoZeroPage`), `NativeCodeGenerator.cpp` (`NativeRegisterDataSet`,
  `GlobalRegisterXYCheck`, `LoopRegisterXYMap`, `CompressTemporaries`).
- SDCC: `src/SDCCralloc.hpp`, `src/mos6502/ralloc2.cc`. Krause, *Optimal Register
  Allocation in Polynomial Time*, CC 2013. Krause, *Bytewise Register Allocation*,
  SCOPES 2015.
- KickC: `Pass4RegisterUpliftPotentialRegisterAnalysis`,
  `Pass4RegisterUpliftCombinations`, `Pass4MemoryCoalesceCallGraph`.
- Cai & Goharshady, *Efficient Algorithms for Partial Constraint Satisfaction Problems
  over Control-flow Graphs*, arXiv 2602.03588 (2026).
- Appel & George, *Optimal Spilling for CISC Machines with Few Registers*, PLDI 2001.
  Scholz & Eckstein, *Register Allocation for Irregular Architectures* (PBQP),
  LCTES 2002. Thorup, *All Structured Programs have Small Tree Width*, 1998.
- C-Bench-64 (github.com/thred/c-bench-64) for cross-compiler numbers.
