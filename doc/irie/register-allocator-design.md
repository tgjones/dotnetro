# A register allocator for the 6502 — design

**Status:** proposal; the steering decisions are recorded in §7. S0, S1, S2 and S3
are done (progress below the stage table in §5); nothing of S4–S7 is implemented.
**Goal:** the best code we can get, measured in bytes and in cycles (frequency
weighted). How fast the compiler runs matters, but comes second.

This document starts from the problem rather than from any existing allocator. It
uses what the current Irie allocator, llvm-mos, SDCC, Oscar64 and KickC do as
evidence, not as templates. §2 gives the evidence, §3 the design, §5 a staged plan,
and §7 the questions that need a decision.

---

## 1. What the problem actually is on the 6502

On a modern CPU the hard question is *fit*: K registers, more live values than K,
so who goes to memory. Once zero page is treated as registers, the 6502 has the
opposite problem:

- **Within one function, fit is free.** There is always another zero-page byte, so
  spilling inside a function almost never happens.
- **Zero page is scarce across the whole program.** A DotNetro program never
  returns to BASIC, so it owns the language workspace: `$00–$8F` on the BBC Micro
  and `$02–$8F` on the C64, about 140 bytes (§7). That budget is shared by every
  function, so this is a *whole-program* layout problem, not a per-function one.
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

So on this machine, register allocation does not decide *whether* a value fits. It
decides three things:

1. **Which values ever reach memory**, and which live only in A, X, Y or a flag.
2. **For each instruction, which form to use and where its operands are:** is the
   counter `INX` or `INC zp`? Is the compare `CMP` or `CPY`? Is the shift `ASL A`
   or `ASL zp`? Is the load `LDA abs,X` or `LDA (zp),Y`? This is instruction
   selection and placement as **one problem**.
3. **Where each memory-resident value lives:** zero page or absolute memory, and at
   which address, chosen across the whole program.

Every allocator in this repo's history (graph colouring, greedy, SSA) aimed at the
modern-CPU problem. The 6502 problem was then patched in with register classes,
hints, funnels and post-pass rewrites.

## 2. Evidence

### 2.1 How much of Irie's gap register allocation can close

We compared Irie with a *backend-only* llvm-mos baseline: llvm's middle-end
disabled, `mem2reg` only, i.e. the same MIR shape Irie sees. Each excess
instruction in 12 corpus cases was classified by cause. The classification is
manual, ±2 instructions per case.

| Cause | Excess instructions | Share |
|---|--:|--:|
| Allocation: placement, coalescing, spills, phi copies | 69 | 33% |
| Selection and allocation together: the op exists on several locations but isel pins one (shift A/zp, INC on X/Y/zp, CPX/CPY, index in X/Y) | 48 | 23% |
| Other instruction selection (signed compare, constant folding into `#imm`, known-zero bytes) | 50 | 24% |
| Legalizer (i16 div/rem through the i32 helper) | 22 | 11% |
| Peephole / combine | 14 | 7% |
| Block layout | 5 | 2% |

The design in §3 targets the first two rows: **about 56% of the excess on the C
corpus.**

C#-shaped code is worse. On 9 C# kernels Irie emits 1.59× the backend-only
baseline:
- Instructions in blocks containing only phi copies and a jump: **218**, which is
  **47% of the gap**.
- zp↔zp copies: 256, against 90.
- `INC`/`DEC`/`INX`/`INY`: 0, against 41.
- A/X/Y share of register traffic: Irie uses A for 77%, X for 3%; llvm-mos uses
  A 52%, X 18%.

Recurring failures, each seen in several cases:

| Pattern | What happens |
|---|---|
| Arguments evicted | Incoming zp arguments are moved elsewhere so their registers can hold temporaries. In `pressure-i16` this is a 12-instruction pure permutation. |
| Phi webs not coalesced | Phi copies are duplicated on every edge that was split and rotate between slots each iteration (C# `Sum`: ×4). |
| Everything goes through A | Every ALU op is pinned to A, so a 16-bit shift costs 17 instructions where 8 would do (`ASL zp`/`ROL zp`). |
| Loop counters in zp | Counters live in zp as 16-bit add chains (7 instructions instead of `INY / BNE / INX`). |
| Fixed pointer pair | Every `(zp),Y` copies its pointer into the fixed `$12/$13` pair first. |
| Poor spill handling | Spills are reloaded although the value never moved, and reloads are not folded into the `ADC` operand. |

### 2.2 What the existing allocator is

Iterated-coalescing graph colouring with a per-node class intersection. A/X/Y are
chosen by one tuned rule: a value live across ≤ 3 ALU ops goes to X/Y/A, anything
longer goes to zp first. There is no cost model and no loop weighting.

Constraints are expressed in three overlapping ways: vreg class annotation,
per-operand classes, and a "copy-only" widening rule. Around a dozen passes exist
mainly to cooperate with or repair the allocator:
- `InsertRelocationCopiesForConstrainedDefs`, `TrySplitToRegister`
- `TwoAddressInstruction`, `PhiElimination`'s split-every-edge copies
- `RematerializableSink`, `ParallelCopy`
- `IncrementStrengthReduction`, which pattern-matches the allocator's output shape
- `AddressingModeSelector`, whose `cmp → cpx/cpy` path is unreachable: widening the
  compare's register class to Axy made the corpus 8 instructions worse, because the
  allocator has no cost model
- `RegisterScavenging`, the only reason the "≤ 3" rule exists

The zero-page pool is a fixed 28 bytes. RC20–31 are callee-saved through
`LDA zp; PHA` (8 bytes per register). The `(zp),Y` base is pinned to RC18/19 and
managed by the selector, and that cache leaks across blocks: a miscompile, tracked
as a task.

### 2.3 Prior art

| Compiler | Approach | Result (C-Bench-64, C64) |
|---|---|---|
| **Oscar64** | Zero-page temps coloured by interference, with an interprocedural zp layout. A is never allocated. About 130 passes over the generated 6502 code, in a fixpoint schedule, then recover A/X/Y reuse, using value tracking over A/X/Y and every zp byte, and X/Y promotion by greedy scoring. Zp is re-coloured byte by byte after optimisation. Aggressive integer range narrowing. | **Fastest and smallest** on almost every benchmark |
| **SDCC** | Optimal (bounded) DP over a tree decomposition of the control-flow graph. Bytewise variables. Cost = a dry run of the real code generator. A/X/Y only; zp is plain static memory, overlaid only for leaf functions. | Small code, 2–6× slower than Oscar64 |
| **KickC** | Equivalence classes. Exhaustive "uplift" over A/X/Y/zp combinations, each scored by full code generation. | Excellent on small programs; doesn't scale |
| **llvm-mos** | Greedy plus hints today. Its planned design: conventional SSA, then zp "imaginary" homes coloured first, then SDCC-style DP for hardware placement. Only the first stage is built; the spill strategy changed within weeks. Whole-program static stack and zp promotion by benefit. | Mid-pack speed, large binaries |
| **cc65** | Soft stack plus peepholes | The floor |

Oscar64 is now a second reference on the scoreboard
([llvm-mos-comparison.md](llvm-mos-comparison.md), via `irie-report generate-oscar64`).
On the 35 corpus cases that Irie, llvm-mos and Oscar64 all compile, Oscar64 emits
**975** instructions and **1727** bytes, against Irie's **1584** and **2521**: Irie is
**+62%** in instructions and **+46%** in bytes. Over the full paired corpus, by
contrast, llvm-mos is within 2% of Irie in instructions. Oscar64's code is the best evidence for what this design
aims at. Its crc8 is 30 instructions (llvm-mos 48, Irie 80): the CRC lives in A, the
bit counter in X, and the bit test is the carry out of `ASL`. Calls to the multiply
helper pass arguments in fixed zero page with nothing saved around them. Part of
Oscar64's lead is whole-program knowledge (it picks each function's convention and
propagates constant return values), which §3.7–§3.8 also give Irie.

Conclusions:

1. **Memory placement across the whole program matters at least as much as A/X/Y
   choice.** Oscar64 wins with a greedy A/X/Y story and an excellent memory story;
   SDCC has optimal A/X/Y and a poor memory story.
2. **Cost should come from the code generator itself**, not from hints (SDCC,
   KickC). llvm-mos's hint tables and its `MOSInsertCopies` "to paper over
   coalescing" are what happens otherwise.
3. **Choose the instruction form together with the placement.** llvm-mos fixes
   `CMP GPR,Imag8` before allocation and pays `STY tmp; CPX tmp` in loops.
4. **Allocate per byte.** .NET widens everything to i32, and dead or constant high
   bytes must cost nothing.
5. **Oscar64 deserves the most attention: deciding up front what it recovers
   afterwards.** Its 130 passes are "allocate pessimistically, then win it back".
   A search over register contents that decides the same things up front should
   reach the same quality with far less machinery, *if* the cost model is honest.

---

## 3. The design

### 3.1 Overview

```
legalized byte-level SSA MIR
  → shape selection          (isel, minus register pinning: picks operations, keeps fusions)
  → home classes             (aggressive coalescing of phi webs / copies into shared memory homes)
  → PLACEMENT                (per function, callee-first: register contents + instruction variant
                              at every program point; transfers, stores, reloads, phi moves)
  → home slot assignment     (per function, byte-granular colouring of home live ranges)
  → program layout           (whole program: zp vs abs by benefit, call-graph stacking, static frames)
  → emission + checking      (placement verifier, then the existing late passes)
```

Five pieces, each replaceable on its own:

| Piece | Decides | Kind of problem |
|---|---|---|
| Shape selection | which target *operation* implements each MIR op | pattern matching (exists today) |
| Home classes | which values may share one memory home | interference, no K |
| **Placement** | what A, X, Y, C and N/Z hold at each point; which variant each operation uses; when each value is stored and reloaded | cost-minimising search |
| Home slots | which function-local slot each home class uses | interval colouring |
| Program layout | which slots go to zp, which to absolute memory, at what address | whole-program knapsack plus call-graph stacking |

Functions are processed **callee-first** (bottom-up over the strongly connected
components of the call graph). So when a caller is placed, each callee's entry and
exit conventions and its zero-page footprint are already known (§3.7, §3.8).
DotNetro compiles a closed world, so the whole call graph is known; virtual and
delegate targets are known from the vtables the translator builds.

### 3.2 Operations with location variants

Today isel commits each op to a concrete opcode with a register class: `adc.zp`
with `$a` tied, `cmp` with `Ac` + `Imag8`. In the new design isel commits only to
an **operation**. Each operation has a table of **variants**, and placement picks
one.

A variant says:
- where each operand must be: `A`, `X`, `Y`, `C`, `home`, `imm`, `abs(sym)`, `zp-pair`;
- where each result appears;
- the instruction template it emits;
- its exact bytes and cycles;
- what it clobbers;
- what N/Z and C hold afterwards.

Examples (cost = bytes / cycles):

| Operation | Variant | Template | Cost | After |
|---|---|---|---|---|
| `add8 a, b, cin → r, cout` | a∈A, b∈home | `ADC zp` | 2/3 | r∈A, cout∈C, NZ=r |
| | a∈A, b∈imm | `ADC #` | 2/2 | ″ |
| | a∈A, b∈abs(sym) | `ADC abs` | 3/4 | ″ |
| | commuted: b∈A, a∈home | `ADC zp` | 2/3 | ″ |
| `inc8 a → r` (cin known 0, cout dead) | a∈X, r∈X | `INX` | 1/2 | NZ=r |
| | a∈Y | `INY` | 1/2 | ″ |
| | a∈home, r same home (a dies) | `INC zp` | 2/5 | ″ |
| | a∈A | `CLC; ADC #1` | 3/4 | ″ |
| `shl8 a → r, cout` | a∈A | `ASL A` | 1/2 | cout∈C, NZ=r |
| | a∈home, r same home | `ASL zp` | 2/5 | ″ |
| `cmp8-branch a, b` | a∈A, b∈home/imm/abs | `CMP …; Bcc` | | |
| | a∈X | `CPX …; Bcc` | | |
| | a∈Y | `CPY …; Bcc` | | |
| | b=0 and NZ already reflects a (eq/ne) | `Bcc` | 2/2–3 | |
| `load8 [p + k]` | p∈zp-pair, k in Y | `LDA (zp),Y` | 2/5 | r∈A |
| `load8 [sym + i]` | i∈X | `LDA abs,X` (also `LDY abs,X`) | 3/4 | r∈A (Y) |
| | i∈Y | `LDA abs,Y` / `LDX abs,Y` | 3/4 | r∈A (X) |
| `store8 v → home` | v∈A/X/Y | `STA/STX/STY zp` | 2/3 | |

**One table is the single source of truth.** Emission uses the templates, and costs
are derived from them through the opcode table, which already knows sizes; a cycle
column has to be added. The search can therefore never disagree with what is
emitted. This is SDCC's "dry run" idea without running the code generator inside
the search.

Shape selection keeps what it does well today: fusing a compare with its branch,
folding a single-use symbol load into an `ADC abs`, building multi-byte ladders. It
stops choosing registers.

### 3.3 Locations and state

The allocation unit is the **byte**: the legalizer already splits values into i8
pieces. The extra kinds of value are:
- **carry bits** (i1);
- **pointers**, whose two bytes must sit in an adjacent zero-page pair *whenever* a
  `(zp),Y` variant uses them;
- **constants and symbol addresses**, which are free to rematerialise: their
  "home" is an immediate.

At every program point, placement tracks a **state**:

```
A, X, Y ∈ { a live value, a known constant, ⊥ }
C       ∈ { a live i1 value, known 0/1, ⊥ }
NZ      ∈ { "reflects value v", ⊥ }
H       ⊆ live values whose memory home holds the current value (monotone: SSA values never change)
invariant: every live non-constant value is in A, X, Y, C or H
```

The state is the value-tracking Oscar64 does after the fact (`NativeRegisterDataSet`),
but here it *drives* decisions instead of repairing them. It also covers what
today's separate passes do:
- the `cmp #0` removal in late optimisation (the NZ field);
- the `LDA #imm` → `TAX`/`INX` rewrite (the known-constant fields);
- scavenging (free registers are visible in the state).

### 3.4 Placement within a block: a beam dynamic program

Walk each block's operations in order. Keep the best **K** states, each with its
accumulated cost and a back-pointer. For each operation:

1. **Choose a variant.** Keep only variants whose operand locations can be reached
   from the state.
2. **Make the state match the variant, at minimum cost.** The moves available are
   `TAX`/`TXA`/`TAY`/`TYA`, `LDA`/`LDX`/`LDY` from home or immediate, and
   `STA`/`STX`/`STY` to home (a store adds the value to H). X↔Y goes through A,
   which must be free or saved; that cost is visible here, not guessed afterwards.
   The register domain is tiny (3 registers, C, NZ), so the cheapest move sequence
   comes from a precomputed table or a small exact search.
3. **Apply the variant.** Results go to their locations; clobbered registers become
   ⊥; values whose last use was this operation leave the state.
4. **Score:** `cost += bytes × w_size + cycles × w_speed × freq(block)`.
5. **Prune** to the best K states. States that agree on A/X/Y/C/NZ and differ only
   in H are merged by dominance.

Two rules keep this simple and always solvable:

- **A store is just a move.** The search decides when a value is written to its
  home: right after its definition, just before a clobber, or never (a value that
  lives only in registers needs no home at all). There is no separate spiller.
- **"Everything in its home, registers empty" is always reachable.** Every
  operation has a variant that runs from that state using only A as scratch. So a
  solution always exists, and K = 1 degenerates to Oscar64-style naïve code: a
  correct baseline.

K is a compile-time/quality knob; SDCC's `--max-allocs-per-node` is the same idea.
With K = ∞ on small functions, the search becomes an **oracle** for measuring how
much the heuristic loses (§5, S7).

### 3.5 Placement across blocks: hot blocks decide, cold edges pay

Phi copies are removed from the IR entirely. A block parameter is a value defined
at the block's entry. Moving the arguments into it is part of **reconciling** the
predecessor's exit state with the successor's entry state: a parallel move over
A/X/Y/C/homes, solved exactly because the domain is so small.

The order in which blocks are placed:

1. **Innermost loops first, then outward. Within a loop, follow the body in
   order.** A loop header's entry state is chosen once. Try the best few candidate
   header states, run the body for each, and keep the candidate with the lowest
   *(body + back-edge reconciliation)* cost. The back-edge cost is weighted by the
   loop's frequency, so a counter that stays in X around the loop wins.
2. **Then the remaining blocks in decreasing frequency.** A block whose neighbours
   are already placed starts from (or must end at) their states. Where the states
   disagree, the reconciliation goes on the edge:
   - at the end of the predecessor if it has one successor;
   - else at the start of the successor if it has one predecessor;
   - else in a new edge block, **only if the move is non-empty**. This removes the
     duplicated split-edge copies of §2.1.
3. **Frequency** comes from loop depth (`8^depth`, refined later by static branch
   heuristics). Irie has no loop or frequency analysis today, so this is a new
   analysis.

Single-predecessor blocks with parameters (e.g. the i32 compare ladder in C# `Sum`)
cost nothing: their arguments are already where the predecessor left them.

This order is heuristic. The exact alternative is SDCC/Krause's DP over a tree
decomposition of the CFG, or the 2026 series-parallel-loop formulation
(Cai & Goharshady). Both work on the same state and transition model, so they are an
upgrade *inside* this piece, worth doing only if the oracle shows that block joins
lose significant code.

### 3.6 Home classes and home slots

**Home classes are formed before placement, by coalescing as aggressively as
possible.** In a register file with no limit K, coalescing two values that do not
interfere can never cause a spill. Its only cost is zp footprint, which the layout
step manages.

So every phi web and every copy chain whose values do not interfere becomes one
class. Interference is based on values, so two names for the same value do not
interfere. Moving between two values of the same class is free in the placement
search, because they share an address. This replaces George/Briggs conservatism,
which existed only because K was finite, and fixes the argument eviction and
rotating phi slots of §2.1.

**Home slots are assigned after placement.** A home's live range runs from its
first store to its last reload, which is usually much shorter than the value's
range. Home classes that never interfere share a slot.

- Colouring is per byte, at function scope.
- Pointer homes that a `(zp),Y` variant uses need an **adjacent pair**: colour them
  first, as two-byte pieces.
- After the late passes, run colouring again over the bytes still in use. This
  reclaims bytes the optimisations made dead (Oscar64's `CompressTemporaries`).

Each slot records whether it is **live across a call** and its **benefit**: the
frequency-weighted accesses saved by putting it in zp instead of absolute memory.
Placement knows every access it emitted, so the benefit is exact.

### 3.7 Program layout: zero page as a whole-program resource

Every slot of every function competes for one zero-page budget, set per target.
The layout extends today's `StaticFrameColorer` and `ReentrancyAnalysis`:

1. **Tier choice.**
   - **Must be zp:** pointer pairs used by `(zp),Y`. Their bytes are reserved first.
   - **Everything else:** greedy by benefit per byte into zp; the rest goes to
     absolute memory.
   - Every variant that names a home has an absolute twin (`LDA abs`, `INC abs`), so
     **demoting a slot never invalidates placement.** It only changes the address,
     at +1 byte and +1 cycle per access.
2. **Call-graph stacking, separately inside each tier** (Oscar64's
   `MapCallerSavedTemps`, generalised):
   - A slot **not** live across any call may overlap the frames of the function's
     callees, because it is dead whenever they run.
   - A slot **live across a call** goes above the highest frame of any callee, so
     **nothing is ever saved or restored around a call**.
   - Independent subtrees reuse the same bytes, so the budget used is the deepest
     call path, not the sum over all functions.
   - This replaces the RC20–31 callee-saved PHA/PLA scheme.
3. **Recursion.** A recursive SCC is one node in the stack. Around a call that stays
   inside the SCC, the caller pushes only the bytes live across that call (`PHA`, or
   the soft stack for large frames); the call variant carries that cost, so
   placement sees it. Address-taken locals of recursive functions keep the soft
   stack (RC0/RC1), as today.
4. **Indirect calls.** Functions that can be called through the same vtable slot or
   delegate type form a **convention class**. They share a calling convention
   (§3.8), and an indirect call's callee set for stacking is the class.
5. **Interrupts.** No DotNetro program installs an interrupt handler today, and
   none is needed for input (see §7, decision 5). When handlers arrive (raster or
   vsync handlers, music players), the layout already has a place for them:
   - a handler is a separate call-graph **root** that can preempt anything, so its
     slots go above every other frame and never overlap them;
   - a function reachable from both the main program and a handler can be active
     twice, so it is treated as reentrant: cloned for the handler, or given a soft
     stack frame;
   - the handler's entry and exit save the A/X/Y it uses (on the BBC Micro the MOS
     has already saved A in `&FC`; X and Y are the handler's job).
   The OS's own interrupt code uses `$90–$FF` on both machines, so it never touches
   the `$00–$8F` budget.

### 3.8 Per-function calling conventions

Because the world is closed and placement runs callee-first, **the callee chooses
its own convention**. When placing the callee:
- its entry state is left free, so the search puts each parameter wherever the body
  wants it: a counter in X, a pointer in its zp pair, a byte in A;
- its exit state likewise places the return value.

These become the callee's convention. At each call site, the call is an operation
with exactly one variant: "arguments in these locations; clobbers A, X, Y, flags and
the callee's footprint". So setting up arguments is ordinary placement.

Arguments that go into the callee's homes are written straight into the callee's
slots, which are in the callee's region of the stack, so there are no copies at the
call boundary.

**Fixed conventions remain for:**
- convention classes (indirect calls), where one convention is chosen for the class
  using the members' summed preferences;
- `runtime.irie` functions, `InternalCall` externs and the entry point;
- OS calls, which already have the `bbcmicro_os` and `c64_kernal` conventions.

### 3.9 Flags

- **C** is a location. Carry chains, `ASL`→`BCS` bit tests (crc8), and booleans that
  are left in C for a branch are all choices the search can make.
- **NZ** is a "reflects value v" fact. A compare against zero is a variant that
  costs nothing when the state already reflects v.
- Today a compare and its branch must be adjacent (a moved copy between them would
  clobber N/Z). That restriction goes: the state models N/Z explicitly.

### 3.10 Verification

- **A translation-validation checker, independent of the allocator.**
  - Replay the emitted code symbolically, tracking which SSA value each register,
    flag and slot holds.
  - Join states at block entries.
  - Check that every operand reads the value it should.
  - This catches wrong placement, clobbered values and bad edge moves, whatever
    produced them. It replaces the current interference-based assignment verifier,
    which is tied to the colouring model.
- **Execution.** Corpus `.irie` cases run on the emulator with checked results.
- **The K = ∞ oracle,** to measure optimality (§3.4).

---

## 4. What happens to today's code

| Today | Under this design |
|---|---|
| `GraphColouringAllocator`, `RegisterAllocatorPass` (relocation copies, split-to-register, spill loop) | Replaced by placement + home slots |
| `TwoAddressInstructionPass` | Gone: a tied def is a variant's result location |
| `PhiEliminationPass` (split every edge) | Gone: block-boundary reconciliation (§3.5) |
| `OperandLegalizationPass`, isel "funnels" | Gone |
| `RematerializableSinkPass` | Gone: constants are free to rematerialise |
| `MOS6502ParallelCopyPass` | Gone: argument setup is placement |
| `RegisterScavengingPass` | Gone: the search sees free registers |
| `MOS6502AddressingModeSelectorPass` | Gone: the variant fixes the addressing mode |
| `MOS6502IncrementStrengthReductionPass` | Gone: `INC`/`INX` are variants |
| `MOS6502LateOptimizationPass` (`cmp #0` fold) | Mostly subsumed by the NZ state; kept as a safety net at first |
| `CopyEliminationPass`, `CopyPropagationPass` | Kept at first (cheap); expected to become no-ops |
| PEI callee-saved PHA/PLA | Gone for non-recursive code (call-graph stacking) |
| `ReentrancyAnalysis`, `StaticFrameColorer`, `MOS6502FramePlacementPass` | Extended into program layout |
| RC18/19 pointer scratch, isel pointer cache | Gone: pointers are values with pair homes |
| `MOS6502InstructionSelector` | Kept for shape selection; stops pinning register classes |

That is roughly 4–5K lines out, and more coupled passes than that, replaced by four
components with clear inputs and outputs.

**This departs from llvm-mos's staging.** Selection and placement become one
decision instead of a GlobalISel selection followed by a register allocator. It is
the same move llvm-mos's own planned `MOSRegAlloc` makes ("accounting for the costs
of various instructions against different placements of their operands"). The
difference is that here the variant table makes that the primary mechanism rather
than a repair.

## 5. Staged plan

Every stage leaves the tests green, and every stage is worth having even if the next
one is never built. Stages S1 and S2 do not depend on each other.

| Stage | What | Exit criterion |
|---|---|---|
| **S0** Foundations | (a) Execute the corpus `.irie` cases on the emulator. (b) A cycle metric, if Aemula exposes one, next to bytes in `irie-report`. (c) Loop and frequency analysis. (d) The placement verifier from §3.10, run first on *today's* allocator output. (e) The cheap measured win: drop block parameters of single-predecessor blocks before isel (−32 instructions on C# `Sum`). | Verifier passes the whole suite; C# benchmarks reported in bytes and cycles |
| **S1** Program layout with today's allocator (done, see below) | Unbounded per-function zp registers, then call-graph stacking and a per-target budget; zp vs abs by benefit; callee-saved PHA/PLA removed for non-recursive functions; convention classes for indirect calls | `many-calls`, `stack-args`, `pressure-*` and the C# set fewer bytes; no case worse |
| **S2** Variant tables | Operation + variant table for every byte op; isel emits operations; a trivial placement (K = 1, everything homed) emits correct naïve code end to end | All `.cs` and executed `.irie` tests pass on the naïve path |
| **S3** Placement within blocks | The beam DP of §3.4, with "everything homed" at block boundaries | Better than today's allocator on the 12 studied cases |
| **S4** Placement across blocks | Loop-first boundary negotiation, phi via reconciliation, home classes. **Delete** the colourer and the passes in §4 | Scoreboard total and C# set better than today; no case worse by more than 2 instructions without a written reason |
| **S5** Home slots | Byte-granular colouring, pointer pairs, recolouring after the late passes | zp footprint per program reported and lower |
| **S6** Per-function conventions | Callee-chosen entry and exit placement | Measured on call-heavy C# benchmarks |
| **S7** Tuning | Beam width, frequency weights, size/speed weights; the K = ∞ oracle on small functions; tree-decomposition DP only if the oracle shows block joins lose significantly | Documented gap to the oracle |

**S0 progress (2026-10-17):**
- (a) done: every corpus case executes (`irie-exec`, with `buf(N)` pointer arguments
  and a comparison of all globals after the run). Passing stack arguments needed
  caller-side support in call lowering: the function reserves the largest outgoing
  area in its prologue and the soft stack pointer (RC0/RC1) is set up at startup.
- (b) done as `irie-exec` output: the cycles of the function under test, summed over
  its vectors, counted by the emulator (`--measure`; one CPU tick is one cycle).
  Not yet a column in `irie-report`, which has no reference cycles to compare with.
- (e) done: `SinglePredecessorBlockParamsPass`. Four corpus cases shrink (gcd −12
  instructions). `SpillI64Loop` grows by 16: the extra block-exit permutation is
  an allocator weakness at extreme pressure, which S4 replaces.
- (c) done: `LoopAnalysis` gives natural loops, depth and a static `8^depth`
  block frequency. No pass uses it yet; S3/S4 will.
- (d) done: `PlacementVerifier` replays the allocator's output and checks every
  register and spill-slot read against the value it should hold, independent of
  the allocator's intervals. It runs at the end of `RegisterAllocatorPass` and
  found no violations in the suite, the executed corpus or the `.cs` tests.

S0 is complete.

**S1 progress (2026-10-17):** done, with today's graph-colouring allocator.
- **Homes.** The allocator's cross-call pool is now an unbounded-in-spirit set of
  function-local zero-page *homes* (`$h0…`, 96 per function) instead of the fixed
  callee-saved `RC20–RC31`. RC2–RC17 remain the free, caller-saved registers; RC0–RC19
  stay the fixed ABI window. No register is callee-saved any more, and no function
  saves or restores anything in its prologue/epilogue.
- **Layout.** `MOS6502ProgramLayoutPass` (replacing `MOS6502FramePlacementPass`)
  places each function's block of homes, plus its zero-page frame slots, in
  `$14–$8F` (C64 `$16–$8F`) above the blocks of all its callees (`StaticFrameColorer`
  over the new `CallGraph`), then rewrites each home to its final `RC(n)`. Independent
  call subtrees overlap, so the budget used is the deepest call path. The highest
  program in the test suite (`Int64Memory.cs`) reaches `$41`.
- **Recursion.** Functions of one strongly connected component of the call graph
  share one block; `MOS6502RecursionSavePass` saves the live homes with `PHA`/`PLA`
  around calls that stay inside the component (`RecursionLiveAcrossCalls.cs` covers
  self, mutual and virtual-call recursion).
- **Indirect calls.** An indirect call (the trampoline, whose target pair moved to
  RC16/RC17) reaches only *address-taken* functions (vtable and other initializer
  references, `mem.symbol`), not every function. A call of the trampoline is also now
  seen as an indirect call by `ReentrancyAnalysis`; after lowering it looked like a
  direct call of the trampoline, so a cycle through a vtable could be missed.
  Convention classes by signature were not needed and are not built.
- **Overflow.** Frame slots that do not fit are demoted to absolute memory and the
  layout redone (as before). Homes have no absolute twin yet (that needs the variant
  tables of S2), so a program whose deepest call path needs more than the window gets
  a `NotSupportedException` (a `Diagnostic` from `CompilerDriver`) naming the
  function. Nothing in the suite comes near it. Choosing zero page versus absolute
  memory *by benefit*, and splitting homes into "live across a call" and "not", were
  left out because the measured budget is not binding.
- **Result** (bytes of `irie` corpus code with `iriec --target mos6502 --emit=bin`,
  and of the whole C# lit programs with `dnrc`, which include the reachable corelib):

  | Set | Before | After |
  |---|--:|--:|
  | `LlvmMosReference` corpus (`.irie` cases) | 4119 | 3660 (−11%) |
  | C# lit programs (`.cs`) | 203682 | 179750 (−11.8%) |
  | C# benchmark kernels (`benchmarks.md`) | 5870 | 4619 (−21%) |

  `many-calls` loses its 12-register save/restore, `factorial-recursive` and the
  C# call-heavy code its prologue/epilogue pushes, and the `jsr; rts` pairs they hid
  fold into tail calls.
- **Exit criterion not met in four places.** Three corpus cases (`array-index` +2
  bytes, `two-pointer-copy` +9, `crc8` +8) and one benchmark kernel
  (`ObjectFields.Dist1_Point` +9) are slightly larger. No C# lit program is. The
  difference is register *choice*, not extra work: the larger pool changes the `K`
  that the coalescing tests and the select order use, and an argument byte that used
  to stay in `X` is now stored to a home. It is sensitive to the pool size rather than
  to anything systematic (a pool of 12 homes has no regression on either set, but the
  C# set then gains 6.1% instead of 11.8%); S4 replaces the colouring that makes the
  difference, so it was left alone.

**S2 progress (2026-10-18):** done, as the §3.1 shape (selection and placement
are separate passes), next to the colouring path, selected by `--placement naive`
(`PlacementStrategy.Naive`; `iriec`, `dnrc`, `irie-exec`).
- **Shape selection.** `MOS6502ShapeSelector` turns the legalized generic MIR into
  the unpinned byte operations of the `mos6502` dialect on typed vregs: `adc`, `sbc`,
  `and`, `ora`, `eor`, `shle`, `lshre` (the shifts were already unpinned), `cmp_br`,
  `cmp`, `load`, `store`, `const`. Where a name had a pinned family (`adc.zp`,
  `and.imm`, …) the unpinned op takes its first part; the ones with no single pinned
  op are new (`cmp_br` is §3.2's fused compare-and-branch: one compare of a byte
  pair, then the exits taken on its flags, so a wide compare is one `cmp_br` per
  byte; `cmp` is the carry a CMP leaves, the sign seed of an arithmetic shift
  right; `load`/`store` take `@sym+off` or `%lo, %hi, off`; `const` is an immediate
  or the low/high byte of a symbol). The old `MOS6502InstructionSelector` is
  untouched and stays until S4.
- **Variant table.** `MOS6502Variants` lists, per operation, the variants — operand
  places (`A X Y Carry Home Imm Abs Pair`) and the instruction template — and
  derives bytes from the opcode tables and cycles from a new column,
  `MOS6502InstructionDescription.Cycles` (NMOS base cycles; a page crossing and a
  taken branch are not counted). Emission instantiates the template, so a
  placement's cost and its code cannot disagree; a test pins the table's bytes to
  the encoder's operand sizes. It holds more rows than the naive placement uses
  (compares in X and Y, the `Abs` forms, …) so S3 starts from a costed table.
- **Naive placement.** `MOS6502NaivePlacementPass` is K = 1 with everything homed:
  every non-constant byte and every block parameter gets a home, an operation loads
  its operands (immediates for constants) into A, runs its A-based variant and stores
  the result. A carry the very next operation consumes stays in C (tracked); any
  other is stored as 0/1 and reloaded with `LDA ; LSR`. Phi elimination is gone on
  this path: a branch writes the parameters' homes (through temporary homes when an
  argument is a parameter also being written) and a conditional exit with arguments
  leaves through a block that does the moves. Argument and result copies for calls
  and returns are ordered so that no register is clobbered before it is read.
  Hand-written runtime code, already in physical registers, passes through.
- **Absolute twins of homes** (the S1 gap). `MOS6502ProgramLayoutPass` takes
  `allowAbsoluteHomes`: a function's homes that do not fit the zero-page budget live
  in bytes of absolute memory (`<function>_h<n>` globals) and each instruction naming
  one is rewritten to its absolute form. Zero page goes to the homes accessed most,
  weighted by `LoopAnalysis` frequency; when a call path overflows, the callee with
  the most zero-page homes gives them up first. The colouring path still fails with a
  `NotSupportedException` there.
- **Along the way.** `CallGraph` took any instruction with a symbol operand and a side
  effect for a call, so `lda.imm.symlo @f` (a function pointer being built) was a call
  of `f`, and `f` was never address-taken: an indirect caller's homes could then
  overlap `f`'s. (`CalliDispatchTable` caught it; the colouring path only escaped
  because it keeps almost nothing in homes.) Dialects now say which ops call
  (`Dialect.IsCall`). The reference interpreter gained `addi_with_carry` /
  `subi_with_borrow`, so hand-written narrow cases can be executed.
- **Exit criterion met.** Every `.cs` lit test (BBC Micro and C64 runs) and every
  executed `.irie` case passes on the naive path: `LitTestNaivePlacement` re-runs the
  former with `dnrc --placement naive`, `ExecLitTestNaivePlacement` the latter with
  `irie-exec --placement naive`. `ShapeSelector-*.irie` and `NaivePlacement-*.irie`
  pin the shapes and the code, including a stored carry, edge moves with a swap, and
  a function with more byte values than zero page has bytes.
- **Cost of the baseline** (the number S3 has to beat; same measurements as S1):

  | Set | Colouring | Naive |
  |---|--:|--:|
  | `LlvmMosReference` corpus code, bytes (43 cases both compile) | 3660 | 5076 (+39%) |
  | C# lit programs, bytes (114 programs) | 170208 | 237403 (+39%) |
  | C# benchmark kernels, bytes (6 programs) | 12704 | 17958 (+41%) |
  | Corpus cycles of the functions under test, summed (43 functions) | 5 903 825 | 6 395 491 (+8%) |

  The cycle sum is dominated by the multiply/divide libcalls, which are the same on
  both paths, so it understates the gap for code that does its own arithmetic
  (`loop-sum-array`: 18 955 against 25 908).
- **Left for later stages.** One home per value, no sharing (S5); no folding of a
  symbol load into `ADC abs` and no `INC`/`DEC`/`INX`/`INY` variants (the table has
  the costs, nothing selects them before S3); no placement-specific verifier — the
  post-placement verifier and the executed tests are the check until S3.

**S3 progress (2026-10-18):** done, as a third strategy, `--placement beam`
(`PlacementStrategy.Beam`; `iriec`, `dnrc`, `irie-exec`), next to the colouring and
naive paths. The exit criterion is **not met**: beam placement is far better than the
naive baseline but still larger than the colouring allocator, for the reason the
stage's own design predicts (see the result).
- **The search.** `MOS6502BeamPlacementPass` runs a beam search (K = 8) over the
  operations between two barriers. A state is what A, X, Y, C and N/Z hold, what the
  scratch pointer pair holds, which values are in their homes, and which arguments are
  still in the zero-page registers they arrived in. For each operation every variant
  of `MOS6502Variants` (operands in this register or that) turns a state into a
  successor by a fixed sequence of moves: save what the variant overwrites, put the
  operands in place, emit the variant. A value is stored to its home lazily, when
  something is about to overwrite its only copy; the save is either a store or a
  transfer to a free register (`TAY`), and both are tried. Costs are
  `bytes × w_size + cycles × w_speed × block frequency` (both weights 1; options on
  `BeamPlacementOptions`), taken from the variant table, so the cost of a placement is
  what it emits. K = 1 gives 4068 corpus bytes, K = 4 gives 4000, and K = 8, 16 and 32
  give 3993 and below, so 8 is enough here.
- **Where it searches.** Within a block: ALU, shifts, compares, `inc`/`dec`, loads and
  stores through a symbol or a pointer pair, the runs of copies that set up a call or
  a return (a parallel move into A, X, Y and the argument registers), and `cf.br`.
  Barriers (calls, frame accesses, the hand-written runtime) are placed as the naive
  placement does. Across blocks nothing changed from S2: every live value is in its
  home and the registers are empty. The pointer pair is a tracked location, so a second
  access through the same pointer sets nothing up.
- **What S2's table could not use, now available.** `mos6502.inc` / `dec` come from
  shape selection (a byte add or subtract of one) and have `INX`, `INY` and
  `CLC; ADC #1` variants; the transfers `TAX`, `TAY`, `TXA`, `TYA` are variants too.
  Arguments that arrive in zero-page registers stay there for the whole function when
  it has no call and no copy into a fixed register (they are their own homes), and are
  otherwise read from there until the first thing that could overwrite them. A `br`
  stores its arguments into the parameters' homes straight from wherever they are.
- **Independent check.** `PlacementChecker` runs every block symbolically twice, as
  shape MIR and as the emitted code, and compares the stores, calls, branches (with the
  flags they test), the return registers and, on every exit, the home of every value
  live into the successor (§3.10, per block). It runs on the naive and the beam
  placement of every compile (`IRIE_NO_PLACEMENT_CHECK` turns it off; the cost is
  below the noise of a compile). It found no fault in the naive placement, which
  validates the checker, and it flags the four faults the tests found in the beam
  placement during development when they are put back: a save-by-move that clobbered
  the pointer offset in Y or the source of the transfer that follows, a flag overwritten
  between a load and the branch on it, and an edge move that read the wrong home. Blocks
  holding an operation it does not model are left unchecked, silently.
- **Fixed on the way.** `LdaImmSymLo/Hi`, `LdxImmSymLo/Hi`, `INX`/`INY`/`DEX`/`DEY` and
  `INC`/`DEC zp` were not declared as writing N and Z, so the late optimisation could
  drop a `cmp #0` over a load of an address byte that had overwritten the flags.
- **Result** (same measurements as S1 and S2):

  | Set | Colouring | Naive | Beam |
  |---|--:|--:|--:|
  | `LlvmMosReference` corpus code, bytes (43 cases) | 3660 | 5076 | 3985 (+8.9%) |
  | C# lit programs, bytes (114 programs) | 170208 | 237403 | 187417 (+10.1%) |
  | C# benchmark kernels, bytes (6 programs) | 12704 | 17958 | 14009 (+10.3%) |
  | Corpus cycles of the functions under test (43 functions) | 5 903 825 | 6 395 491 | 6 073 692 (+2.9%) |

  Against colouring, beam placement is smaller on 7 corpus cases (`bitops`,
  `shift-const`, `loop-sum-array`, `array-index`, `two-pointer-copy`, `many-calls`,
  `pressure-i16`) and equal on 11 (every straight-line function: the arguments stay
  where they arrive and the result is built where it is returned); it is faster on
  7 and equal on 12. It is larger on the other 25, which are all functions with several
  blocks, and the loss is the block boundary: `crc8` is 230 bytes against 155,
  `early-return` 87 against 39, because every value that crosses a boundary is stored
  and reloaded and every phi is a store of the parameter's home.
- **Left for later stages.** Placement across blocks and home classes (S4) are what
  close that gap: the values of a loop header kept in registers, and a phi sharing the
  home of its argument. Not done and not needed to show the gap: the memory-destination
  forms (`INC zp`, `ASL zp`; they only pay once a result can share its operand's
  home, which is S4), the `abs,X` / `abs,Y` loads and stores (the selector would have to
  fold an index into the address), edge moves from registers for the exits of a
  `cmp_br`, and callee-chosen conventions (S6).

**S4 progress (2026-10-18):** in steps, each committed on its own. Colouring is deleted
only at the end, and only if the exit criterion is met (decided with the maintainer).
Per-case colouring baselines (bytes, and cycles for the corpus) were recorded before
anything changed, since the colourer cannot be rerun once it is gone: corpus 3660 bytes
and 5 903 825 cycles, C# lit and benchmark programs 182 912 bytes (matching the S1 and S3
tables).
- **S4.1, home classes (done).** `HomeClasses` (beam placement only; the naive baseline
  keeps one home per value) joins every block parameter with the arguments it receives,
  and every vreg-to-vreg copy with its source, when the two are never live together.
  Interference is by value (a copy does not make source and result interfere) and is
  built from a backward scan with the block's live-out set; candidates are tried in
  decreasing block frequency (`LoopAnalysis`), and merged classes keep an interference
  set so a class is checked once. Values that live in a zero-page argument register or
  are rebuilt from a symbol keep their own home. `Placer.HomeKey` maps a vreg to its
  class; `EdgeMoves` drops a move whose argument already shares the parameter's home
  (the argument must still be in that home, which `Jump` makes sure of), a branch that
  needs no moves no longer gets an edge block, and the move hazard test compares homes
  rather than vregs. `PlacementChecker` accepts a home that has not been touched yet
  when the value it must hold is the input of a vreg it shares with.

  | Set | Colouring | Beam (S3) | Beam + home classes |
  |---|--:|--:|--:|
  | `LlvmMosReference` corpus code, bytes (43 cases) | 3660 | 3985 | 3831 (+4.7%) |
  | C# lit programs and benchmark kernels, bytes (120 programs) | 182 912 | 201 426 | 172 321 (−5.8%) |
  | Corpus cycles of the functions under test | 5 903 825 | 6 073 692 | 5 985 762 (+1.4%) |

  Against colouring the corpus is smaller on 9 cases, equal on 11 and larger on 23, and
  16 of the 120 C# programs are larger. Copy coalescing on top of phi coalescing changes
  no byte count and saves 6K corpus cycles.

- **S4.2, block entry and exit states (done for single-predecessor blocks).** A block
  with exactly one incoming edge, from a block placed before it, and no parameters
  *inherits*: it starts from the registers its predecessor left on that edge (A, X, Y,
  the carry, the scratch pointer pair, which values have their home written) instead of
  from empty registers with everything homed. Every other block (joins, loop headers, the
  entry) keeps the old convention. Blocks are now placed in reverse post-order, so a
  predecessor is placed first.
  - *Exit use.* A value that flows into a successor is needed up to the branch that goes
    there (`BlockInfo.ExitUse`), not to the end of the block (it used to be pinned live to
    the end of the block). `StoreForExits` stores, before a branch, only what a
    *non-inheriting* successor needs (its live-in values and arguments); an inheriting
    successor takes the registers as they are, except for a value that lives in its
    zero-page argument register, which has no home to inherit.
  - *Snapshots.* The search records, for each branch to an inheriting block, what the
    registers hold at that point (`Snapshot`); the winning path's snapshots become the
    entry states. A branch to an inheriting successor is placed both ways, leaving the
    values in registers and storing them first, and states that handed over different
    things are kept apart in the beam.
  - *Lookahead.* A candidate end state is charged for the first stretch of each inheriting
    successor, searched from the registers it leaves (`BeamPlacementOptions.Lookahead`,
    default 3 levels), so the exit state is chosen for what comes next. A `JMP` out of a
    block that has code is charged 3 bytes and 3 cycles; a block that is only a jump
    disappears in branch folding and is free.
  - *Checker.* `PlacementChecker` is one instance per function, so terms are shared
    across blocks. An inheriting block is checked from its predecessor's symbolic exit
    state, renamed to the inputs the block's expectation names (live-in vregs that hold
    the same value are one input). Control flow into an inheriting block no longer
    checks homes on the edge; the block's own reads and its own exits do.
  - *Not done: N/Z across the edge.* Only the carry is inherited. The late optimisation
    that drops `cmp #0` after a load or transfer works within one block, so a state that
    claimed N/Z were inherited would cost compares the code still emits.

  | Set | Colouring | Beam + home classes | + inherited entry |
  |---|--:|--:|--:|
  | `LlvmMosReference` corpus code, bytes | 3660 | 3831 | 3821 (+4.4%) |
  | C# lit programs and benchmark kernels, bytes | 182 912 | 172 321 | 171 753 (−6.1%) |
  | Corpus cycles of the functions under test | 5 903 825 | 5 985 762 | 5 979 732 (+1.3%) |

  The gain is small because the multi-block cases are dominated by joins and loop
  headers, which still start from homes; that is what S4.3 changes. Against colouring the
  corpus is smaller on 9 cases, equal on 12 and larger on 22; 16 of 120 C# programs are
  larger. `BeamPlacement-Inherit-MachineCode.irie` pins an arm that reads the sum from A.

- **S4.3a, joins and loop headers (done).** A join (a block with several incoming edges,
  loop headers included) now starts from a snapshot too: the one the first edge placed
  into it leaves, its *primary* (the predecessor earliest in reverse post-order, so for a
  loop header the edge from outside). Every other edge is *secondary* and reconciles to
  that state. Only blocks whose edges all come from the branches the search places
  (`cf.br`, `cmp_br`), one edge per predecessor, take part; the rest keep the empty-
  registers convention.
  - *The state of a join* (`Snapshot`) is what A, X and Y hold, by name: a live-in value,
    or a block parameter when the primary's argument sits in the register (and is not
    itself live into the join); and which live-in values and parameters are in their
    homes. Everything not in a register is in its home. The carry and the pointer pair are
    not carried into a join.
  - *Primary edge* (`DefineEntry`): stores what is not in a register, moves the parameters
    that did not stay in a register into their homes, and hands over the registers as
    they are. It is also placed with everything stored and the registers empty (the old
    convention), and the cheaper of the two, judged by the join's first stretch, wins.
  - *Secondary edge* (`SatisfyEntry`): stores the values the join expects in homes, moves
    the parameters, then loads the registers the join expects (Y, X, A in turn). On a
    `cmp_br` exit the code goes in a new block on that edge, placed on a copy of the
    search state, only when it is non-empty; on a `cf.br` it goes before the jump.
  - *Checker.* A block that starts from a snapshot is checked from the snapshot, and
    every edge into it (the primary included) is checked to deliver it
    (`CheckContract`); this replaces the earlier exit-state record and the aliasing of
    live-in vregs that held the same value.
  - Results: corpus 3821 -> 3790 bytes, C# 171 753 -> 170 679 bytes. Loop-heavy cases
    barely move (`crc8` 133 -> 131): the header's state is chosen from its first stretch
    alone, without the cost of the back edge, which S4.3b addresses.

- **S4.3b, loop header states (done, without a loop-body dry run).** The first edge into a
  join is placed seven ways (`ExitMode`): registers left as they are; everything stored
  and registers empty; block parameters loaded into free registers (in parameter order
  or in reverse) with or without storing the live-in values first; and only the live-in
  values stored. A header's state is judged by the cost of its first stretch and of the
  blocks that follow it, down to the latch (`Lookahead`, 3 levels): while looking ahead,
  the back edge pays to reach the state being judged (`_provisional`), so a counter kept
  in X is judged by what the back edge then costs. This stands in for placing the loop
  body once per candidate; a loop whose body is deeper than the lookahead does not see
  its back edge. A loop whose counter is a byte now keeps it in a register
  (`BeamPlacement-LoopCounter-MachineCode.irie`: the low byte of the counter in Y).

  | Set | Colouring | after S4.3a | after S4.3b |
  |---|--:|--:|--:|
  | `LlvmMosReference` corpus code, bytes | 3660 | 3790 | 3719 (+1.6%) |
  | C# lit programs and benchmark kernels, bytes | 182 912 | 170 679 | 169 374 (−7.4%) |
  | Corpus cycles of the functions under test | 5 903 825 | 5 976 654 | 5 962 929 (+1.0%) |

  The 22 corpus cases that are still larger than colouring are mostly straight-line code
  (`stack-args` +61 bytes, `pressure-high` +50, `chain-arith` +18, `struct-pass` +16);
  their gap is inside one block, which cross-block placement cannot close.

### Work outside the allocator that still limits quality

These were measured in §2.1. No allocator closes them, and on C# some are larger
than anything in this document:

- **Range / demanded-bytes narrowing of i32 to i8/i16.** Oscar64's biggest
  structural advantage. Narrowed bytes vanish from both zp and the instruction
  stream.
- **Known-zero bytes.** C# output contains dozens of `AND #$00`.
- **Signed compare** without a materialised `EOR #$80` temporary.
- **Middle-end loop work:** loop-invariant code motion (`a.Length` is reloaded every
  iteration), induction-variable narrowing, count-down loops.
- **An i16 div/rem helper.**

## 6. Risks

- **The cost model is everything.** If the variant table is incomplete, the search
  confidently picks the wrong thing. Mitigation: a single table drives both cost
  and emission, and the oracle and the scoreboard catch blind spots.
- **Beam pruning at block joins.** It can lose to an exact method. The tree
  decomposition upgrade lives inside the same piece if needed.
- **Compile time,** in particular for the WebAssembly playground. K and the number
  of candidate loop-header states are the knobs; the K = 1 path is fast.
- **The size of the switch.** S2–S4 replace selection-adjacent code wholesale.
  S2's naïve path exists so that correctness lands before quality, and so the old
  allocator can stay as a comparison until S4.
- **Callee-chosen conventions** optimise the callee's view only. Callers with many
  call sites may want something else. The fix is a second iteration that feeds
  call-site costs back to the callee; deferred to S6/S7.

## 7. Decisions (steering, 2026-10-17)

1. **Zero-page budget: the program owns the language workspace.** A DotNetro program
   never returns to BASIC, so the budget is `$00–$8F` on the BBC Micro (144 bytes)
   and `$02–$8F` on the C64 (142 bytes; `$00/$01` are the 6510 port), less the 2-byte
   soft-stack pointer. The C64 figure is an assumption on the same rule: the KERNAL
   uses `$90–$FF`, and BASIC's use of `$02–$8F` stops mattering once the program
   never returns. Today's layout (RC registers at `$00–$1F`, spill frames at
   `$70–$8F`) becomes one budget managed by program layout (§3.7).
2. **Objective: bytes and frequency-weighted cycles together** by default
   (`bytes × w_size + cycles × freq`, §3.4), with size- and speed-leaning switches.
3. **Selection and placement are merged** (§3.2, §4), departing from the GlobalISel /
   llvm-mos pass structure where this design needs it.
4. **Oscar64 is a second reference** alongside llvm-mos, through `irie-report`
   (`generate-oscar64` writes `<case>.oscar64.asm` beside each corpus case;
   `generate-report` adds its columns and an Irie-vs-Oscar64 total). See §2.3.
5. **No interrupt handlers or threads for now; recursion supported** (§3.7).
   Interrupts are not needed for input:
   - **BBC Micro:** the MOS scans the keyboard in its own interrupt handler, and
     programs read keys with `OSRDCH` / `INKEY` (`OSBYTE &81`). Analogue joysticks
     go through the ADC, read with `OSBYTE &80`. Neither needs user code in an
     interrupt.
   - **C64:** the KERNAL interrupt scans the keyboard (`GETIN`). Joysticks are read
     directly from CIA 1 (`$DC00`/`$DC01`) with plain loads.

   The likely reasons for user handlers later are timing effects rather than
   input: raster interrupts on the C64 (split screens, sprite multiplexing, music
   players) and vsync or timer events on the BBC Micro (`EVNTV`). §3.7 item 5 says
   how layout would handle them; nothing in stages S0–S7 depends on it.

## References

- Irie today: [GraphColouringAllocator.cs](../../src/Irie/Passes/GraphColouringAllocator.cs),
  [RegisterAllocatorPass.cs](../../src/Irie/Passes/RegisterAllocatorPass.cs),
  [StaticFrameColorer.cs](../../src/Irie/Passes/StaticFrameColorer.cs),
  [ReentrancyAnalysis.cs](../../src/Irie/Passes/Analyses/ReentrancyAnalysis.cs),
  [notes/status-2026-10-07.md](../../notes/status-2026-10-07.md) §5–7.
- llvm-mos: `MOSConventionalSSA.cpp`, `MOSImagRegAlloc.cpp` and `MOSRegAlloc.cpp`
  (stub headers describing the planned design), `MOSZeroPageAlloc.cpp`,
  `MOSStaticStackAlloc.cpp`, `MOSRegisterInfo.cpp` (`copyCost`, hints); open PR #611
  (treescan spilling).
- Oscar64 (github.com/drmortalwombat/oscar64): `InterCode.cpp` (`ReduceTemporaries`,
  `MapCallerSavedTemps`, `CheckStaticStack`), `GlobalAnalyzer.cpp` (`CheckFastcall`,
  `AutoZeroPage`), `NativeCodeGenerator.cpp` (`NativeRegisterDataSet`,
  `GlobalRegisterXYCheck`, `LoopRegisterXYMap`, `CompressTemporaries`).
- SDCC: `src/SDCCralloc.hpp`, `src/mos6502/ralloc2.cc`. Krause, *Optimal Register
  Allocation in Polynomial Time*, CC 2013. Krause, *Bytewise Register Allocation*,
  SCOPES 2015.
- KickC: `Pass4RegisterUpliftPotentialRegisterAnalysis`,
  `Pass4RegisterUpliftCombinations`, `Pass4MemoryCoalesceCallGraph`.
- Cai & Goharshady, *Efficient Algorithms for Partial Constraint Satisfaction
  Problems over Control-flow Graphs*, arXiv 2602.03588 (2026).
- Appel & George, *Optimal Spilling for CISC Machines with Few Registers*, PLDI 2001.
  Scholz & Eckstein, *Register Allocation for Irregular Architectures* (PBQP),
  LCTES 2002. Thorup, *All Structured Programs have Small Tree Width*, 1998.
- C-Bench-64 (github.com/thred/c-bench-64) for the cross-compiler numbers in §2.3.
