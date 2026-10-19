# Irie (compiler backend) notes

Loaded when working under `src/Irie`. Detail lives next to the code; this file is the map and the rules that are easy to get wrong.

## Where things are documented

- `Mir/` — the single unified MIR layer: one IR flows through every pass, with only the opcode mix and the level of physreg detail evolving. Text syntax: `MirParser` / `MirWriter` class comments.
- `Dialects/<name>/<Name>Dialect.cs` — each dialect's ops and typing rules (`Arith`, `Mem` incl. the `ptr` type, `Cast`, `Cf`, `Core`, `Pseudo`, `Call`).
- `Passes/` — generic passes and infrastructure. **`Passes/PipelineBuilder.cs` is the pass order**; each pass documents itself.
- `Target/` — generic target abstractions (`Target` lists the hooks a target supplies).
- `Target/MOS6502/README.md` — the 6502 target: legalizer, CC_MOS, shape selection, placement, homes and zero-page layout, subtargets (BBC Micro, C64), library routines, tests. Class comments (`MOS6502Placer`, `MOS6502BeamPlacementPass`, `MOS6502Variants`, `PlacementChecker`, `MOS6502ProgramLayoutPass`, …) hold the rest.
- `MachineCode/` — the low-level layer below MIR, above raw bytes.
- Tools: `iriec` (`Irie.Tools.Compiler`), `irie-exec` (`Irie.Tools.Exec`), `Irie.Tools.Interpreter` (reference interpreter, the oracle for execution checks). Their `--help` lists the flags; `--tune` / `--beam-width` / `--choose-conventions` are the placement knobs.

## Rules

- Add or reorder passes in `PipelineBuilder.AddStandardPipeline` only; `iriec` and `dnrc` share it.
- Anything that will run must come from `Target.EmitProgram` / `BuildProgram` (they link in `libcalls.s`), not `MachineCodeEmitter.Emit`. `--emit=asm|mc` use plain `Emit`, so their listings show only the program's own functions.
- Adding a placement variant (`MOS6502Variants`) or a shape op means extending `PlacementChecker`: an emitted op it does not model is a failure, a shape op it does not model silently skips the block.
- The ABI window RC0..RC19 is fixed and no register is callee-saved (see `MOS6502Registers`, `MOS6502CallLowering`).
- Pointers are `ptr`, not integers: no `arith.*` op takes or produces one (`GenericMirVerifierPass` enforces it).
