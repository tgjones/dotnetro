# ZX Spectrum (Z80) target plan

Goal: `dnrc` / the web playground can compile a C# program to Z80 code and produce a ZX Spectrum 48K snapshot
(`.sna`) that runs in an emulator, with `Console` I/O working and the `.cs` lit tests executing on a Z80 emulator.
This is a second Irie target, `z80`, plus a second system, `zxspectrum`, layered on it (the same split as
`mos6502` / `mos6502-bbcmicro`).

Why Spectrum rather than ZX80: the ROM gives us text output (`RST 16`) and keyboard scanning (`LAST-K`) for free,
the character set is near-ASCII, and a `.sna` snapshot is a fixed-size file with no loader. The ZX80 would need a
custom display-file writer, a non-ASCII charset and a program-file format, for a worse emulator story.

## Findings (verified in the repo, 2026-10-15)

### Already target-neutral — reuse as is

- `PipelineBuilder.AddStandardPipeline(passMgr, target)` drives everything through `Target` hooks: `CallLowering`,
  `LegalizerInfo`, `InstructionSelector`, `PseudoExpander`, `BranchLowering`, `FrameLowering`, `RegisterInfo`,
  `MachineCodeEmitter`, `GetRuntime()`, `DefaultOrigin`, `PackageImage`, and the `Add*Passes` extension slots.
- Generic passes: register allocator (graph colouring, per-node class sets), legalizer + artifact combiner, phi
  elimination, two-address, copy propagation, scavenging, return-merge, tail-duplication, frame lowering /
  access lowering / PEI (target supplies instruction emission via `FrameLowering`).
- Dialects (`arith`, `mem`, `cast`, `cf`, `call`, `pseudo`), MIR text/binary formats, the MachineCode layer.
- IL front end, the CoreLib split (`Foo.cs` shared + `Foo.<Target>.cs` platform half), `TargetRegistry` /
  `TargetDescriptor` (already carries `EmulatorKind`), the playground's `EMULATOR_PANELS` map.
- `FrameSlot.StackId` is an opaque per-target id ("only the owning target knows what a given id means"), so a
  Z80 can add its own frame placements without touching generic code.

### Hardcoded to BBC / 6502 — must be generalised (Stage 0)

- `CompilerDriver.cs:16-17` hardcodes `HostAssemblyResolver.BbcMicro` + `TargetRegistry.BbcMicroId` in the
  file-based `Compile`; `IlToMirTranslator.cs:73` (file-path ctor) does the same.
- `Irie.csproj:8` embeds `Target/MOS6502/runtime.irie` as the only runtime resource.
- `EcmaMethod.cs:66` (comment) and the translator route `InternalCall` methods by name into *the* `runtime.irie`;
  with two runtimes, names only need to be unique per target, and the lookup must use the active target's text.
- `ReentrancyAnalysis` / `StaticFrameColorer` are generic code but their model is the 6502's: non-reentrant
  functions get static (zero-page) locals. The Z80 has no zero page; see Stage 3 for how to use it.
- `Dialect.IsReturn`, `BranchLowering.TryGetUnconditionalBranchTarget` etc. are already hooks — no change.
- Text in `Dialects/Call/CallOp.cs` (indirect-call trampoline "parks the pointer in zero-page slots"),
  `LegalizerPass.cs:148` (8-bit element width) and `IlToMirTranslator.cs:~1598` (divide/remainder libcall names
  "same as `MOS6502LegalizerInfo.LowerLibcall`") assume 6502. Audit whether any is behaviour, not just comment.

### Web emulator options (checked 2026-10-15)

| Option | License | Notes |
|---|---|---|
| JSSpeccy 3 (`gasman/jsspeccy3`) | GPL-3.0 | WASM core in a Worker; 48K/128K/Pentagon; ROMs bundled in `static/roms/`. `runtime/jsspeccy.js` has `pause()`, `reset()`, `setMachine()`, `loadSnapshotFromStruct()`; worker messages `keyDown`/`keyUp`/`reset`/`loadSnapshot`/`loadMemory`; `runtime/snapshot.js` has `parseSNAFile`. Last push 2025-12-28. No npm package; README says the snapshot struct is undocumented. |
| `floooh/chips` | zlib | Header-only C, ZX 48K/128K system, builds to WASM with Emscripten. Active (pushed 2026-09-26). We would write our own JS wrapper. |
| zxplay | GPL-3.0 | JSSpeccy3 fork, archived 2026-06-20, URL-parameter driven. Not a base. |
| ZX Generation, ZX84, jsZX | MIT / ? / ? | Immature or unverified; not considered. |

**Recommendation:** start with JSSpeccy 3 (works today, SNA path is in its source), vendored/forked so we can
reach `pause`/`keyDown`/`keyUp`/`reset` rather than depending on its UI. Re-evaluate chips if the GPL-3.0
licence is a problem for the playground or JSSpeccy proves hard to drive.

## Decisions

1. **Image format: `.sna` (48K).** 27-byte register header + 49152 bytes RAM. In the 48K variant `PC` is not in the
   header: it is pushed on the stack in the RAM image and restored by `RETN`. We write the stack so `RETN` lands on
   our entry point. No BASIC loader, no tape traps. `.tap` (BASIC loader + CODE block) is a later addition.
2. **Origin `0x8000`** (uncontended RAM; `0x4000-0x7FFF` is slowed by the ULA). Stack at the top of RAM,
   `0xFFFF` downward. The `.sna` header's `SP` and the pushed return address are set by `PackageImage`.
3. **ROM dependence is minimal and isolated.** `PutChar` = `LD A,ch; RST 16` after opening channel 2 once at
   start; `ReadLine` polls `LAST-K`. Everything lives in `runtime.irie`, so replacing the ROM later is local.
   The test emulator stubs `RST 16` in C#, mirroring the BBC OS stubs, so lit tests need no ROM.
   Interrupts: leave IM1 and the ROM's 50 Hz interrupt on (it maintains `LAST-K` / `FRAMES`); runtime code must
   not clobber `IY` (ROM expects `IY = 0x5C3A` at interrupt time). This rules `IY` out as a frame pointer
   unless interrupts are disabled around its use — use `IX` (see Stage 3).
4. **Register model.** Byte registers `A,B,C,D,E,H,L`; pairs `BC,DE,HL`; index `IX` (and `IX/IY` halves are
   undocumented — not used). Flags `Z,C,S,P/V,N,H` as first-class physregs, like the 6502's flags. 16-bit pairs
   are real register classes (unlike the 6502), so `i16` is *legal*, not narrowed to bytes; `i32`/`i64` narrow to
   16-bit halves. This is the largest design difference from the 6502 target and drives the legalizer rules.
5. **Calling convention:** first 16-bit arg in `HL`, second in `DE`, third in `BC`, remaining on the stack;
   8-bit args in `A`, `E`, `C`; returns in `A` / `HL` / `DE:HL`. Callee-saved: `IX`, `IY` (the ROM requires
   `IY`). Decide and document in `Z80CallLowering`; mirror how `MOS6502CallLowering` is structured.
6. **Reference compiler:** SDCC and llvm-z80 for design questions, in the same spirit as the llvm-mos rule in
   `CLAUDE.md`. Check whether a local llvm-z80 checkout is available before Stage 3; if not, SDCC output is the
   fallback for "what does a good sequence look like". There is no `irie-report` corpus for Z80 and none is
   planned in this effort.

## Stages

Each stage ends green under `dotnet test --solution src` in Debug and Release.

### Stage 0 — Make the target a parameter (small, benefits any new target)

- [ ] Add `TargetDescriptor` members for what is currently implicit: `CoreLibId` (resolver id), `RuntimeResource`
      (or have `Target.GetRuntime()` be the single source — already abstract; make sure the translator calls it).
- [ ] `CompilerDriver.Compile(assemblyPath, entryPoint)` and the `IlToMirTranslator` file-path ctor take a target
      id (default `bbcmicro` so `dnrc` behaviour is unchanged); add `--target` to `dnrc`.
- [ ] `Irie.csproj`: embed each target's runtime as its own resource; `Z80Target.GetRuntime()` reads its own.
- [ ] `HostAssemblyResolver(targetId)` already exists; add a static for the new target when it arrives.
- [ ] Audit the three comment-vs-behaviour sites listed above; fix any that are behaviour.
- [ ] Verify: existing tests unchanged; add a test that an unknown `--target` reports a clear error.

### Stage 1 — Z80 machine-code layer (assembler, encoder, disassembly)

- [ ] `Target/Z80/Z80Registers.cs`, `Z80Opcode.cs`, `Z80Op.cs` — op set for the MachineCode layer first
      (`ld r,r'`, `ld r,n`, `ld rr,nn`, `ld a,(nn)`, `ld (hl),r`, `ld r,(ix+d)`, `add/adc/sub/sbc/and/or/xor/cp`,
      `inc/dec`, `add hl,rr`, `sbc hl,rr`, `push/pop`, `jp/jr/djnz/call/ret` incl. conditions, `rlca…`, shifts,
      `ldir`, `ex de,hl`, `cpl`, `scf`, `ccf`…).
- [ ] `Z80AssemblyWriter` / `Z80AssemblyParser` (traditional `LD A,(HL)` syntax) for the MachineCode layer, wired
      into `TargetDescriptor.WriteAssembly`.
- [ ] `Z80BinaryEncoder`: two-pass like `MOS6502BinaryEncoder` — layout, then encode; `JR` range checks (signed
      8-bit) with an error, not silent relaxation (no relaxation yet; add `JR`→`JP` only when a program needs it).
- [ ] Unit tests: encode/decode round trips for every encoding form; `.s` lit tests for assembler output.

### Stage 2 — Z80 test emulator (needed before the backend can be tested end to end)

- [ ] Add a Z80 core under `lib/` (own implementation or a vetted permissively-licensed one; no .NET Z80 core has
      been evaluated yet — decision to make here). Must cover the documented instruction set including
      `DD`-prefixed `IX` forms, `ED` block ops and flags (`H`, `N`, `P/V`).
- [ ] `DotNetro.Compiler.Tests.Emulator`: new `ITargetSystem` `ZxSpectrumTargetSystem` (load at `0x8000`, set
      `SP`, stub `RST 16` → console output, stub `LAST-K` polling to feed `--input`, tick budget as for BBC).
      `--target-system zxspectrum`; `--trace` writes PC/AF/BC/DE/HL/SP per instruction.
- [ ] Core self-test: a small set of hand-assembled programs run via the `Z80AssemblyParser` + `Z80BinaryEncoder`.
      A well-known conformance ROM (e.g. ZEXDOC) is optional but worth running once.

### Stage 3 — Z80 backend (the bulk of the work)

Work incrementally; each sub-step gets `.irie` lit tests with complete `CHECK` listings per `CLAUDE.md` (generate
with `dotlit --regenerate`).

- [ ] **3a Dialect + register info:** `Z80Dialect`/`Z80InstructionInfo` (operand classes, tied operands, implicit
      flag defs/uses), `Z80RegisterClass` / `Z80RegisterInfo` (`a`, `r8`, `bc/de/hl/ix` pair classes, flag classes).
- [ ] **3b Legalizer:** `i8`, `i16` legal for add/sub/logic/shifts-by-constant; `i32` → two `i16` halves with
      carry chain (`adc hl,rr`); variable shifts and mul/div/rem → libcalls into the runtime (generalise the
      libcall naming from the 6502 so both targets share the mechanism); `cast.*`, `ptradd`, `mem.load/store` for
      `i8/i16/i32`.
- [ ] **3c Instruction selector:** arithmetic, compare + conditional branch fusion (`cp` + `jp cc`), loads and
      stores (`ld a,(hl)`, `ld (nn),hl`, `ld r,(ix+d)`), pointer arithmetic via `add hl,rr`, `mem.fill` /
      block copy via `ldir`/`lddr`, `call`/`ret`.
- [ ] **3d Call lowering + frames:** `Z80CallLowering`; `Z80FrameLowering`. Decide the local-variable strategy:
      (i) reuse the static-frame colorer — non-reentrant functions get absolute-address locals, which suits Z80
      absolute `ld a,(nn)`/`ld (nn),a` and costs no frame pointer; (ii) reentrant functions use an `IX`-based
      stack frame (`ld a,(ix+d)`), `IX` saved/restored in prologue/epilogue. Start with (i) only plus a clear
      compile error for recursion, then add (ii) when a lit test needs recursion. `FrameSlot.StackId` carries the
      placement; `FrameLowering.LowerFrameAccess` emits the form. (The "zero page" part of the static-frame model
      becomes "a RAM region at a fixed address" — confirm `StaticFrameColorer` has no zero-page-specific limit.)
- [ ] **3e Pseudo expansion + post-RA passes:** `Z80PseudoExpander` (copies between byte regs, pair moves via
      two byte moves or `push/pop`, `ex de,hl`), `Z80BranchLowering` (`jp`/`jr`), block placement / branch folder
      (re-use MOS6502's if it is generic enough to move into `Passes/`; if only partly, duplicate first and
      factor after the second consumer exists).
- [ ] **3f MachineCode emitter + `Z80MachineCodeEmitTable`:** same shape as the 6502 emit table, throws on
      unmapped ops.
- [ ] **3g Target class:** `Z80Target` (chip), `ZxSpectrumTarget : Z80Target` (`DefaultOrigin = 0x8000`,
      `PackageImage` → `.sna`). Register in `PipelineBuilder` consumers via `Target.Add*Passes` hooks only.
- [ ] Exit criterion: the existing `.irie` lit corpus has Z80 counterparts for the arithmetic / branch / call /
      memory cases, and a hand-written "return 42" MIR program runs in the test emulator.

### Stage 4 — Spectrum system layer

- [ ] `DotNetro.CoreLib.ZxSpectrum` project + `Console.ZxSpectrum.cs` (platform half of `Console`).
- [ ] `Z80/runtime.irie` (Spectrum-specific part): `start` (set `SP`, open channel 2, call entry, then `halt`
      loop), `PutChar` (`RST 16`, CR→`0x0D`), `PlatformWriteLineInt32`, `PlatformReadLine` (poll `LAST-K` with
      echo), `PlatformBeep` (`BEEP` ROM routine or port `0xFE` loop), `StringBytes`, indirect-call trampoline
      (`jp (hl)` — simpler than the 6502's), plus the mul/div/rem libcalls from 3b.
- [ ] `ZxSpectrumSnaWriter` (internal, like `BbcMicroDfsImage`): `.sna` = header with `SP` pointing at the pushed
      entry address, interrupts enabled, `IM 1`, border colour; RAM image `0x4000..0xFFFF` with the system-variable
      area initialised from a canned post-boot snapshot (so the ROM interrupt handler and `RST 16` work: the
      simplest robust approach is to embed a ~few-hundred-byte template of the `0x5C00-0x5CB5` system
      variables and the channel tables captured from a freshly booted 48K, rather than computing them).
      Unit tests: header fields, size is exactly 49179, entry address round trips through `RETN`.
- [ ] `TargetRegistry`: add `ZxSpectrumId = "zxspectrum"`, `EmulatorKind: "jsspeccy"`, with `Encode`/`WriteAssembly`
      from Stage 1.
- [ ] Port the `.cs` lit corpus to run on both targets (the `RUN` lines choose `--target`); tests that
      depend on BBC-specific behaviour stay BBC-only. `DIFF: dotnet emulated` keeps working.

### Stage 5 — Playground integration

- [ ] Vendor JSSpeccy 3 (GPL-3.0; confirm the playground's licensing, record it in `NOTICE`/docs) under
      `ClientApp/` as a built artefact or a patched fork exposing `pause()`, `reset()`, `keyDown`/`keyUp`,
      `loadSnapshotFromStruct` — all exist in `runtime/jsspeccy.js` / `worker.js` already. Includes the ROM
      (`static/roms/48.rom`): confirm redistribution terms before shipping (Amstrad has permitted this for
      emulators historically; verify the current wording).
- [ ] `panels/zxSpectrumMachine.ts` with the same surface as `BbcMicroMachine` (`boot(image)`, `run`, `pause`,
      `isRunning`, `keyDown`, `keyUp`, `clearKeys`); `panels/emulatorPanel.ts` generalised (or a sibling
      `zxSpectrumPanel.ts`) and `EMULATOR_PANELS` extended with `jsspeccy`.
- [ ] Parse the `.sna` in the browser the way `runtime/snapshot.js` `parseSNAFile` does, or hand the bytes to
      `openUrl`/`loadSnapshotFromStruct` — pick whichever is stable after reading the vendored source.
- [ ] Target picker in the playground: new `TargetInfo` entry comes from `TargetRegistry` already.
- [ ] Fallback if JSSpeccy proves awkward to drive: wrap `floooh/chips` `zx.h` (zlib) via Emscripten; keep the
      `ZxSpectrumMachine` interface identical so the panel does not change.

## Risks / open questions

- **Backend size.** The 6502 target is ~8K lines. The Z80 has a richer register file and 16-bit pairs, so
  register classes and selection patterns are more complex, but the 16-bit-native ops should shrink the legalizer.
  Expect Stage 3 to dominate the schedule; keep it incremental and test-led.
- **`IY` and interrupts.** The ROM interrupt handler needs `IY = 0x5C3A`. Using `IY` or disabling interrupts
  anywhere in generated code has to be a conscious choice. Default: do not use `IY`; generated code never
  executes `DI`.
- **Contended memory timing** only changes speed, not results; ignore for correctness, note it for samples.
- **`ReentrancyAnalysis` conservatism** treats every function as a potential indirect-call target, so any program
  using delegates/interface dispatch will be "reentrant". Fine while the corpus is non-recursive; Stage 3d(ii)
  (the `IX` frame) is what makes it a non-issue later.
- **ROM licensing and GPL-3.0** (JSSpeccy) need a decision before Stage 5 ships, not before it starts.
- **Z80 core for the tests** (Stage 2): build vs adopt is undecided and no candidate has been evaluated.
- **`.sna` system-variable template** (Stage 4): the exact bytes must be captured from a booted 48K; verify the
  snapshot runs in JSSpeccy (and ideally a second emulator) before relying on it.

## Suggested order and checkpoints

0 → 1 → 2 (a Z80 assembler + emulator, usable by hand-written tests) → 3a–3g (backend) → 4 (system + `.sna`) →
5 (playground). Stage 0 is independent and can land on its own at any time. After Stage 2, re-check the plan: if
the Z80 core is slow or the encoder is more work than expected, adjust Stage 3's scope before starting it.
