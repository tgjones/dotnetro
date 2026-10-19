# MOS6502 target

How Irie turns generic MIR into 6502 code. The classes named here carry the detail in
their own comments; this page is the map. The pass order itself is in
`Passes/PipelineBuilder.cs`.

## From generic MIR to bytes

1. **Legalization** (`MOS6502LegalizerInfo`) narrows wide arithmetic into byte
   operations (`arith.addi i32` becomes four `arith.addi_with_carry i8`, with the
   `pseudo.merge`/`unmerge` pairs collapsed by the artifact combiner). Constant
   shifts become chains of `mos6502.shle`/`lshre`. Multiply, divide, remainder and
   variable-count shifts become calls to the library routines (below).
2. **ABI lowering** uses `MOS6502CallLowering`: CC_MOS, bytes assigned in order A, X,
   RC2, RC3, … (LSB first for multi-byte values). The `bbcmicro_os` and `c64_kernal`
   conventions put byte arguments in A, X, Y for ROM entry points.
3. **Shape selection** (`MOS6502ShapeSelector`) turns the generic ops into the
   *unpinned* byte operations of the `mos6502` dialect on typed vregs. They have no
   register class and no addressing mode: `adc`/`sbc` (`%r, %cout = adc %a, %b, %cin`),
   `and`/`ora`/`eor`, `shle`/`lshre`, `inc`/`dec`, `cmp` (the carry a CMP leaves: the
   sign seed of an arithmetic shift right), `cmp_br` (one compare, then the exits taken
   on its flags: `cmp_br %a, %b, cc, T [, ne, T2]`, conditions `eq ne cs cc mi pl`),
   `load`/`store` (`@sym+off`, or `%lo, %hi, off` through a pointer pair) and `const`
   (an immediate, or `@sym, 0|1` for the address bytes). `pseudo.return` becomes
   `mos6502.rts`.
4. **Placement** (`MOS6502BeamPlacementPass`, on the shared `MOS6502Placer`) gives every
   non-constant byte, and every block parameter, a *home* and chooses the registers.
   There is no phi elimination, register-class pinning or register allocation: block
   parameters are homes, and a branch writes its arguments into the parameters' homes.
   `MOS6502Variants` is the single table of what an operation can be implemented as.
   `PlacementChecker` validates every placed block.
5. **The tail**: frame lowering, branch folding, `MOS6502RecursionSavePass`,
   `MOS6502ProgramLayoutPass`, frame-access lowering, prologue/epilogue insertion,
   pseudo expansion (`MOS6502PseudoExpander`; impossible pairs such as
   `$x = pseudo.copy $y` go through `$a`), late optimisation, register scavenging,
   block placement (fall-through layout) and tail duplication.

## Homes and zero page

A *home* (`$h0, $h1, …`; `MOS6502Registers.Home`) is a function-local zero-page byte.
It has no address until `MOS6502ProgramLayoutPass` stacks each function's homes in the
target's zero-page window above those of all its callees (`StaticFrameColorer` over
`CallGraph`; the functions of a call-graph cycle share one block) and rewrites it to the
`RC(n)` of its address. So a call never clobbers a live home and nothing is saved
around calls. The exception is recursion: `MOS6502RecursionSavePass` saves live homes
with `PHA`/`PLA` around calls that stay inside the caller's cycle (direct recursion, and
vtable or delegate calls that lead back). `CallGraph` sends an indirect call, including
`jsr @__call_indirect_trampoline`, to every *address-taken* function only.

Homes beyond the zero-page budget (the most accessed, loop-depth weighted, stay) become
bytes of absolute memory (`<func>_h<n>` globals) and every instruction naming one is
rewritten to its absolute twin (`lda.zp` to `lda.abs`, …).

## Subtargets

- `MOS6502BbcMicroTarget` (`mos6502-bbcmicro`): origin `$2000`, homes and promoted frame
  slots in `$14–$8F`, `PackageImage` wraps the bytes in a single-file Acorn DFS `.ssd`
  (`BbcMicroDfsImage`).
- `MOS6502C64Target` (`mos6502-c64`, registry id `c64`): origin `$080D`, `ZeroPageBase`
  2 (RC0/RC1 must not land on the 6510 port at `$00/$01`), homes and static frame slots
  in `$16–$8F`, `PackageImage` produces a `.prg` with a `10 SYS` BASIC stub
  (`C64PrgImage`). The test emulator takes `--target-system c64`, and every `.cs` lit
  test has a `RUN-emulated-c64` line. The CoreLib half is `DotNetro.CoreLib.C64`
  (`Console.C64.cs`: PETSCII via KERNAL `$FFD2`/`$FFCF`).

## Library routines

`libcalls.s` is hand-written MOS6502 assembly, an embedded resource parsed at use by
`MOS6502AssemblyParser` (`__rcN` names imaginary register N). It holds the multiply,
divide/remainder and variable-shift routines the legalizer calls; its header states the
register contract. `Target.EmitProgram` links in the routines a program references.
`Lit/CodeGen/MOS6502/Libcalls-Exec.irie` runs every routine against the interpreter.

## Tests

`Irie.Tests/Lit/CodeGen/MOS6502/`: `ShapeSelector-*.irie` pin the selected shapes,
`GreedyPlacement-*.irie` (`--beam-width 1`) the greedy placement and the absolute homes,
`BeamPlacement-*.irie` the beam placement's code. Every lit test runs the default
pipeline, so the executed corpus (`RUN-exec`) and every `.cs` program in
`DotNetro.Compiler.Tests` exercise it too.
