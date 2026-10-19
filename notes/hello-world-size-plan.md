# Hello world: fewer 6502 instructions

Status: not started. Written 2026-10-19 after reading the code we emit for `Console.WriteLine("Hello, World!")`
(`src/DotNetro.Compiler.Tests/Lit/HelloWorld.cs`). Self-contained.

## Goal

Cut the instruction count of the hello world program. Baseline (BBC, Debug,
`dnrc --assembly hello.dll --emit assembly`): **113 instructions, 241 bytes** (`--emit program`).
Success is measured in instructions and bytes of this program **and of the lit programs**
(see `range-narrowing-plan.md`: its baseline is 151,668 bytes over all `DotNetro.Compiler.Tests/Lit/*.cs`;
no lit program may grow).

To reproduce:

```bash
dotnet build src/DotNetro.Compiler.Tests -v q
src/DotNetro.Compiler.Tests.CsCompiler/bin/Debug/net10.0/DotNetro.Compiler.Tests.CsCompiler \
  src/DotNetro.Compiler.Tests/Lit/HelloWorld.cs > /tmp/hw.dll
src/DotNetro.Compiler.Driver/bin/Debug/net10.0/dnrc --assembly /tmp/hw.dll --emit assembly
```

## Where the 113 instructions go

| Function | Instructions |
|---|---|
| `System_Console_WriteLine_String` | 59 |
| `System_String_get_Length` | 14 |
| `DotNetro_Runtime_Startup_Initialize` | 13 |
| `Program__Main___String_SZArray` | 9 |
| `System_String_GetChars` | 8 |
| `__entry` | 5 |
| `HeapStart` | 3 |
| `__call_indirect_trampoline` | 1 |
| `System_Console_PutChar_Byte` (a `JMP $FFE3` thunk) | 1 |

## Ideas, in order of expected payoff

Savings are hand counts from the listing, not measurements. Do them in this order unless a step
turns out to depend on another.

- [ ] **1. Inliner (~25 instructions, the biggest)**. Details below.
- [ ] **2. Fold `global + constant` into one symbol-relative immediate (~7)**. `Main` builds
  `string0000+2` at run time: `CLC / LDA #<s / ADC #2 / STA $1A / LDA #>s / ADC #0 / TAX / LDA $1A`.
  It should be `LDA #<(s+2) / LDX #>(s+2)`. Applies to every string literal, vtable and static field.
- [ ] **3. Fold constant bytes in the compare ladder (~10)**. The loop head of `WriteLine` compares two
  constants (`LDA #0 / EOR #$80 / TAX / LDA #0 / EOR #$80 / STA $19 / CPX $19`, then `LDA #0 / CMP #0`).
  `0 < remaining` with two known-zero high bytes should be `LDA $17 / ORA $18 / BEQ`. Largely covered by
  `range-narrowing-plan.md`; do not duplicate it, just re-measure hello world after it lands, and
  add a byte-wise constant fold of the compare chain if anything is left.
- [ ] **4. Store to a constant address as `STA zp` / `STA abs` (~6)**. `Startup.Initialize` writes
  `*(ushort*)0 = 0x7C00` through `STX $12 / STX $13 / LDY #0 / STA ($12),Y / ...` where
  `LDA #0 / STA $00 / LDA #$7C / STA $01` is half the size. Same for loads from `inttoptr(constant)`.
- [ ] **5. `INC` / `DEC` idioms for ±1 on multi-byte values (~4)**. The 16-bit `p++` in the loop is
  `CLC / LDA / ADC #1 / STA / LDA / ADC #0 / STA`; `INC lo / BNE + / INC hi` is 3 instructions, and
  the 16-bit `remaining--` can be `LDA lo / BNE + / DEC hi / + DEC lo`.
- [ ] **6. Dead-argument removal and unused-runtime stripping (~6)**. `__entry` passes `LDX #0 / TXA`
  for the `args` parameter `Main` never reads. `HeapStart` and `__call_indirect_trampoline` are emitted
  although nothing references them: link a runtime routine only if the program references it.
- [ ] **7. Dead-store elimination after placement (4)**. `get_Length` stores `A` to `$14` twice with no
  read in between, and copies zeros to `$02`/`$03`. Inlining makes most of this vanish; revisit
  afterwards for what is left in non-inlined code.
- [ ] **8. Pointer in the indirect-addressing pair (~4)**. The loop copies `p` from `$15/$16` to
  `$12/$13` each iteration (`LDX $15 / STX $12 / LDA $16 / STA $13`) just to use `(zp),Y`. Not possible
  while `p` lives across the `PutChar` call; revisit once `PutChar` is inlined to a ROM `JSR`
  (whether the OS call preserves `$12/$13` is a target fact to check, not assume).

## 1. The inliner

### Why it is first

Every call to a small function pays the call, the argument and result marshalling, and the callee's
own code for that. In hello world:

- `get_Length` is 14 instructions plus ~5 at the call site (`LDX / LDA / JSR / STA / STX`). It
  zero-extends a `u16` into the four return bytes (`A`, `X`, `$02`, `$03`), stores to `$14` twice for
  nothing and copies the pointer into `$12/$13`. Inlined it is about 6: `LDY #0 / LDA (p),Y / STA /
  INY / LDA (p),Y / STA`.
- `GetChars` is 8 instructions plus marshalling; inlined it is a 16-bit add (~4).
- `PutChar` is a `JMP $FFE3` thunk; inlined the call is `JSR $FFE3`.
- `Main` and `Initialize` are called once; folding them into `__entry` removes call and return, makes
  the `args` argument dead (idea 6) and exposes the string address to idea 2.

Estimated net: about 25 of 113 instructions. Beyond hello world, the corelib is made of such helpers
(`Length`, `GetChars`, the `PutChar` externs, `Concat`, `Equals`, `IsNullOrEmpty`), so every program
that touches strings pays for them today. No inliner exists (grep for "inline" in `src/Irie` only finds
unrelated uses).

### Design (start conservative, tune later)

- **Where**: a new module-level generic-SSA pass, `InliningPass`, in `PipelineBuilder.AddStandardPipeline`
  **before** `FrameLoweringPass` and `AbiLoweringPass`, so inlined code never goes through argument and
  return marshalling. It runs after `GenericMirVerifierPass` and `ArithSimplifyPass`
  (constants canonical); `ArithSimplifyPass` and `SinglePredecessorBlockParamsPass` then clean up the
  inlined body. A module pass is possible: `Pass.Run(CompilationContext)` takes the whole module,
  `MirFunctionPass` is only a loop over its functions.
- **What is inlined** (first heuristic, all must hold):
  - the call is a direct call (`call.*` with a symbol callee, not `call.indirect`);
  - the callee is not recursive, directly or through a cycle (compute SCCs of the call graph and never
    inline within one);
  - the callee is a defined function in the module (not an `InternalCall` extern routed to
    `runtime.irie`);
  - **either** the callee has exactly one call site and is not address-taken (no vtable slot, no
    `calli` target, no `funcaddr`-style reference), **or** its body is at most N generic ops
    (start with N ≈ 8, a named constant), with no more than a few blocks.
- **Mechanics**: clone the callee's blocks into the caller, remap vregs, frame slots and block ids,
  split the caller block at the call, turn the callee's `return` into a branch to the continuation with
  the result as a block argument, and replace the call's result with that block parameter. Bring over
  any callee frame slots (address-taken locals) as new slots in the caller.
- **Order**: process callees before callers (reverse topological order of the call graph) so a small
  callee that itself calls a small callee is already flat when it is considered. Then drop functions
  that are no longer referenced.
- **Calling conventions**: calls with a non-default convention (`bbcmicro_os`, `c64_kernal`) are
  `call.indirect` of constants and are not inlined (nothing to inline); a callee carrying a
  `--choose-conventions` decision is unaffected, because inlining runs before that choice exists.

### Things to check before writing code

- [ ] How the translator emits calls and returns (`CallDialect`, `CallOp`), and how `InternalCall` externs
  and vtable slots appear in the module, so "address-taken" and "extern" are exact tests.
- [ ] How frame slots (`FrameSlot`) and `MirGlobal`-backed statics are owned by a function, to clone
  them correctly. Recursion-save and static-stack logic in the MOS6502 target assumes a call graph:
  confirm nothing caches it before the pass runs.
- [ ] The placement is callee first (`4419be9`). Check that removing functions and growing callers does
  not break that ordering or the zero-page budget (`--stats`: `zero-page.deepest-path`).
- [ ] Whether the reference interpreter (`Irie.Tools.Interpreter`) needs to run before and after the
  pass as an oracle; it is the soundness check for the rewrite.

### Tests

- [ ] `.irie` lit tests in `src/Irie.Tests/Lit/` for the pass alone (`iriec` with a stop-after flag):
  single call site, small callee with several call sites, callee with a loop (multiple blocks), callee
  with a frame slot, recursion (not inlined), mutual recursion (not inlined), address-taken callee (kept
  but its direct calls inlined if small), callee that is an extern.
- [ ] Add a `.cs` lit test in `src/DotNetro.Compiler.Tests/Lit/` that calls a tiny helper with a
  return value and one with a side effect, so the emulator proves the behaviour is unchanged
  (`DIFF: dotnet emulated`).
- [ ] Regenerate goldens with `dotlit --regenerate` and review every diff: inlining changes many
  existing `CHECK` blocks. A golden that grows is a failure of the heuristic, not a golden to accept.
- [ ] Run `dotnet test --solution src` in Debug and Release.

### Measuring

- [ ] Record hello world and the total over all `Lit/*.cs` (as in `range-narrowing-plan.md`) before
  and after. Success: hello world drops by about 25 instructions and no lit program grows in bytes.
- [ ] Tune N and the single-call-site rule against the same totals; the heuristic is expected to be
  revisited (cost model with call-site count, size of the body vs the call overhead, loop-aware).

## Order of work

1. Inliner (idea 1), then re-measure.
2. Ideas 2 and 4 next: small, independent, and each applies well beyond hello world.
3. Ideas 6 and 5.
4. Re-read the listing after `range-narrowing-plan.md` lands and decide what remains of ideas 3, 7 and 8.

## Out of scope

- Changing what `Console.WriteLine` does (for example a runtime-provided string-print routine). That
  would shrink hello world but moves the cost into the corelib instead of fixing the compiler.
- Interprocedural register allocation beyond what callee-first placement already does.
