# Floating point: `float` / `double` support

Status: not started. Written 2026-10-21 after exploring the code; self-contained.

## Goal

Compile C# programs that use `float` and `double` (arithmetic, comparisons, conversions, fields, arrays,
`Math` / `MathF`, `ToString`) to 6502 code that matches the host .NET runtime's IEEE 754 results bit for bit.
Speed is not a goal; code size and zero cost to programs that do not use floats are. The `DIFF` lit tests
(host runtime vs emulator) are the oracle.

## Decisions (agreed)

- **Operations live in the existing `arith` dialect**, as in MLIR (`arith.addf`, `arith.cmpf`, …), not a new dialect.
  This includes the conversions (`sitofp`, `uitofp`, `fptosi`, `fptoui`, `fpext`, `fptrunc`): they stay in `arith`, not `cast`.
- **Full IEEE 754 behaviour**: round-to-nearest-even, NaN, infinities and denormals handled. No flush-to-zero.
- **Float→int conversions saturate and map NaN to 0** (the .NET 9+ behaviour the host oracle has).
- **f32 first, then f64.** `float` arithmetic uses native f32 routines, never f64 round trips.
- **Deferred**: `ckfinite`, `conv.ovf.*` (they need exceptions), `Math.Sin`/`Cos`/`Pow` and other transcendentals.

## Findings behind the plan

- Missing IL: `ldc.r4/r8`, `conv.r4/r8/r.un`, `ldind/stind/ldelem/stelem .r4/.r8`, `ckfinite`. The arithmetic, compare
  and branch opcodes exist but are integer-only (`TranslateCompare`, `TryGetCompareBranch` says "no floats").
  The type map in `IlToMirTranslator` (~line 660) has no `Single` / `Double`; `PrimitiveType` already has sizes 4 / 8.
- `IRType.cs` has an unused `FloatType(int)`. `MirParser` knows only `i1..i64` / `ptr`. `MOS6502LegalizerInfo.GetAction`
  returns `Unsupported` for any type that is not an integer or pointer.
- `LowerLibcall` (mul/div/rem → `__mulsi3`, …) is the template: a legalizer `Custom` action that calls `_callLowering.LowerCall`
  on a routine in `libcalls.s`. Routines are linked only if referenced.
- `MirInterpreter` keeps values as `ulong` bit patterns, so f32/f64 fit it directly.
- `DotNetro.CoreLib` has no `Single` / `Double` structs, `Math`, or float formatting.
- llvm-mos reference (`compiler-rt/lib/builtins/mos/`): hand-written `addsf3.S` (706 B), `mulsf3.S` (710 B), `divsf3.S`
  (791 B), `subsf3.S` (a trampoline that flips b's sign and jumps to add). Everything else (compare, conversions,
  all of f64) is compiled C. Read it as a design reference; write our own routines.

## Design

### IR

- Types `f32` / `f64` (`FloatType`); parser, writer, verifier and binary MIR learn them.
- New `ArithOp`s, appended last to keep opcode numbering stable:
  `AddF, SubF, MulF, DivF, RemF, CmpF, ConstantF` plus conversions `SIToFP, UIToFP, FPToSI, FPToUI, FPExt, FPTrunc`
  The conversions stay in `arith` (decided), not `cast`.
  `negf` / `absf` are not ops: the front end emits an `arith.xor` / `arith.and` of the sign bit on the bitcast integer.
- `cast.bitcast` between `iN` and `fN` of equal width (a no-op after legalization).
- `ArithCmpFPredicate`: the LLVM set `oeq ogt oge olt ole one ord ueq ugt uge ult ule une uno`.
- `constant` carries the IEEE bit pattern; text form accepts both a decimal literal and a hex bit pattern (round-trip exact).
- Data movement needs no new mem ops: the front end loads/stores as `iN` and bitcasts. Copy, phi / block params, select,
  call args and returns of `fN` are legalized exactly like `iN` of the same width (one mapping in `GetAction`), so all the
  existing narrowing applies.
- Passes that assume integers (`RangeNarrowing`, `RangeFold`, `ArithSimplify`, `ValueRange`) must ignore float vregs and
  ops. Constant folding of float ops uses host `float` / `double` arithmetic, which is bit-exact with the target.

### Legalizer

Each float op becomes a CC_MOS libcall (a `Custom` action modelled on `LowerLibcall`), compiler-rt names:

| Op | f32 | f64 |
|---|---|---|
| add / sub / mul / div | `__addsf3` `__subsf3` `__mulsf3` `__divsf3` | `__adddf3` … |
| rem | `fmodf` | `fmod` |
| int → float | `__floatsisf` `__floatunsisf` `__floatdisf` `__floatundisf` | `__floatsidf` … |
| float → int | `__fixsfsi` `__fixunssfsi` `__fixsfdi` `__fixunssfdi` | `__fixdfsi` … |
| extend / truncate | | `__extendsfdf2` `__truncdfsf2` |
| compare | `__cmpsf` | `__cmpdf` |

- Narrower ints (i8 / i16) widen to the helper's width first, as `LowerLibcall` does.
- **Compare**: one three-way routine per width returning an `i8` (-1 less, 0 equal, +1 greater) with an unordered mode
  (NaN result chosen by the caller, as compiler-rt's `__ltsf2` / `__gtsf2` split does) so that each of the 14 predicates
  becomes one inline `i8` compare. Work out the NaN-mode / compare table once and pin it in the legalizer's class comment.
- `PlacementChecker` / call lowering need nothing new: arguments and results are plain byte sequences.

### Routines (`libcalls.s`)

- Shared unpack / round-and-pack subroutines per width (`JSR` within the entry point's `.zeropage` reservation) to save bytes.
- Each routine: IEEE special cases first (NaN, inf, zero, denormal), then the core, then round-to-nearest-even with
  guard / round / sticky bits.
- f32 multiply: 24×24 shift-add; divide: restoring 24-bit; f64 equivalents at 53 bits (slow, accepted).
- `fmod`: repeated exponent-aligned subtraction.
- Size comparison against llvm-mos' `.S` files after f32 add / mul / div land (use `irie-report`-style measurement);
  beat them where practical.

### Front end (`IlToMirTranslator`)

- Type map: `Single` → `f32`, `Double` → `f64`; `StackValue` gains float types. Locals, args, returns, fields, arrays,
  struct members and `Unsafe` copies of float size already work as integer byte moves once the sizes and types map.
- `add sub mul div rem neg` dispatch on stack type; `ceq cgt cgt.un clt clt.un` and the `b*` / `b*.un` branches map to
  `cmpf` predicates (`.un` = unordered-or). Compare-and-branch keeps its fusion shape (`cmpf` → i1 → `cf.cond_br`).
- `ldc.r4 / r8`, `conv.r4 / r8 / r.un`, `conv.i* / u*` from float, `ldind / stind / ldelem / stelem .r4 / .r8`.
- Constant folding in the translator or `ArithSimplify` for all-constant operands.

### Corelib

- `Single` / `Double` structs in `Primitives.cs` (`ToString`, `Equals`, `GetHashCode`, `IsNaN`, `IsInfinity`, …).
- Shortest round-trip `ToString` (the host's default); decide on the algorithm (Ryu or Grisu with fallback) when
  reaching this phase, weighing code size.
- `Math` and `MathF`: `Abs`, `Floor`, `Ceiling`, `Round`, `Truncate`, `Sqrt`. `Sqrt` is a software routine (a libcall,
  bitwise restoring root on the mantissa). `Floor` / `Ceiling` / `Round` / `Truncate` are plain C# on the bit pattern.
- `BitConverter.SingleToInt32Bits` / `Int32BitsToSingle` / `DoubleToInt64Bits` / `Int64BitsToDouble`: `Unsafe`-style
  reinterpretation or a bitcast intrinsic (`IntrinsicBodies.cs`).

## Phases

- [ ] **1. Irie types and ops.** `f32` / `f64` in parser, writer, verifier, binary MIR; the new `arith` ops, `cmpf`
  predicates and `cast.bitcast`; interpreter semantics (host float math; saturating conversions); generic-pass audit
  (range passes ignore floats); `Irie.Tests` goldens for round trip and interpreter execution.
- [ ] **2. Legalizer.** `GetAction` treats `fN` moves like `iN`; `Custom` libcall lowering for every float op; compare
  predicate expansion; bitcast removal; `*-Legalizer.irie` goldens.
- [ ] **3. f32 routines.** `__addsf3 __subsf3 __mulsf3 __divsf3 __cmpsf` first, then int↔f32 conversions; run each against
  the interpreter in `Libcalls-Exec`-style tests with edge cases (±0, denormals, inf, NaN, rounding ties, overflow,
  underflow) cross-checked with host `float`.
- [ ] **4. Front end for f32.** Type map, arithmetic, compare, branch, conversions, `ldc.r4`, `ldind/stind/ldelem/stelem.r4`,
  constant folding; `.cs` lit tests with `DIFF` (arithmetic, compare, conversions, fields, arrays, NaN / inf cases).
  Regenerate `doc/dotnetro/il-support.md`.
- [ ] **5. Corelib for f32.** `Single`, float `ToString`, `MathF`, `BitConverter`; lit tests printing floats.
- [ ] **6. f64.** Routines (`__adddf3` … `__extendsfdf2`, `__truncdfsf2`, `fmod`), front end `r8` forms, `Double`, `Math`;
  lit tests. `.zeropage` reservations for the 8-byte working registers.
- [ ] **7. Sqrt and rounding helpers**, if not already done in 5 / 6.
- [ ] **8. Size pass.** Compare routine sizes with llvm-mos; share more subroutines; check no existing lit program grew.
- [ ] **9. Docs.** `src/Irie/Target/MOS6502/README.md` (libcall list, float lowering), `src/Irie/AGENTS.md` if the layout
  changes, the arith dialect docs.

## Risks and open questions

- **Opcode numbering:** appended `ArithOp`s must not shift existing values (binary-MIR goldens).
- **`conv.i1` / `conv.u1` from float:** confirm the host's behaviour (saturate to the narrow range vs truncate the int32
  result) with a `DIFF` test before fixing the routine contract.
- **Float `ToString` size** may dominate: programs that never print a float must not link it (it is reached only through
  `Single.ToString` / `Double.ToString`, so discovery should handle this; verify).
- **Eval-stack float type:** ECMA allows higher internal precision; we keep exact f32 / f64 as RyuJIT does. `conv.r.un` goes
  through f64 then to the destination type when needed.
- **Compare NaN-mode table** needs one careful derivation and exhaustive 14-predicate × {less, equal, greater, unordered}
  tests before the front end depends on it.
