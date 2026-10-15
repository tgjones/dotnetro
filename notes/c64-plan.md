# Commodore 64 target plan

Goal: `dnrc` / the web playground can compile a C# program to a C64 `.prg` that runs in an emulator, with `Console`
I/O working and the `.cs` lit tests executing on the 6502 test emulator with C64 KERNAL stubs. This is a new
*system* layered on the existing `mos6502` chip target, the same split as `mos6502` / `mos6502-bbcmicro`:
`MOS6502C64Target : MOS6502Target`, id `c64`. No new backend work: the C64's 6510 is an NMOS 6502 plus an on-chip
I/O port at `$00/$01`.

Related: `notes/zx-spectrum-plan.md` has a "Stage 0 — make the target a parameter" that is the same work as
Stage 0 below. Do it once, whichever target lands first.

## Findings

### Already target-neutral — reuse as is

- The whole pass pipeline, legalizer/selector/RA, `MOS6502Target` (chip codegen, `MOS6502BinaryEncoder`,
  assembly writer/parser), IL front end, CoreLib split (`Foo.cs` + `Foo.<Target>.cs`, built per target by
  `DotNetro.CoreLib.<Target>` projects), `TargetRegistry` / `TargetDescriptor` (carries `EmulatorKind`).
- `MOS6502FramePlacementPass` already lives on the BBC subtarget only, not the chip, so C64 chooses its own window.

### Hardcoded to BBC — must change (verified in the repo)

1. **Zero-page address of `RC(n)` is `n`** (`MOS6502MachineCodeEmitter.cs:90-91`: `phys.Id - RC(0)`). RC0/RC1 are
   the CC_MOS soft stack pointer, so they land on `$00/$01`. On the C64 those are the 6510 data-direction
   register and the memory-map port; storing a stack pointer there remaps ROM/RAM/IO and crashes. **This is the
   one real blocker on the codegen side.** Fix: a target hook `Target.ZeroPageBase` (default 0; C64 = `$02`)
   used by the emitter, `MOS6502FramePlacementPass`, and anything else that turns a zp physreg into an address
   (grep for `RC(0)` and `- 12`). With base `$02`, RC0..RC31 = `$02..$21`.
2. `IlToMirTranslator.BuildEntryFunction` hardcodes BBC "MODE 7" (`VDU 22,7` via `@oswrch`). Needs a target
   hook for the start-up prologue (C64: switch to lower/upper charset, set colours, clear screen).
3. `CompilerDriver.Compile(path, entry)` (line 16-17) and the `IlToMirTranslator` file ctor (line 73) hardcode
   `HostAssemblyResolver.BbcMicro` / `BbcMicroId`; `dnrc` has no `--target`. `Irie.Tools.Compiler/Program.cs:63`
   lists `mos6502-bbcmicro` by name. `Irie.csproj:8` embeds the single `runtime.irie`.
4. `runtime.irie` is one file holding both chip-generic functions (`__call_indirect_trampoline`, mul/div libcalls,
   `StringFromPointer`, `StringBytes`) and BBC OS wrappers (`oswrch`, `PutChar`, `osword`, `PlatformReadLine`,
   `PlatformBeep`, `PlatformWriteLineInt32` — the last calls `PutChar` and is OS-independent in spirit). It must be
   split into a shared 6502 part and a per-system part; `GetRuntime()` returns the concatenation.
5. `ManagedHeap.s_heapPtr = (byte*)0x1000` is a BBC-specific constant in shared CoreLib code. On the C64 `$1000`
   is inside the program. The heap start/end must be per target (see Memory).
6. The `.cs` lit tests' RUN line is `@dnrc --emit program | @emulator --target-system bbcmicro`; C64 needs
   `--target c64` on `dnrc` and `--target-system c64` on the emulator.
7. Playground: `TargetRegistry` already drives the target list; the shell needs a C64 machine (below).
8. The soft stack pointer (RC0/RC1) is never initialised by `__entry` today (BBC test memory is zeroed). Check
   what actually uses it (`CallerStackArg`, >16-byte argument lists) and, if it can be hit, give it a region and
   an init in the C64 start-up. Verify before relying on either answer.

## C64 platform facts (from memory unless marked verified; confirm each before depending on it)

### Memory map (64 KB, 6510)

| Range | Use |
|---|---|
| `$0000-$0001` | 6510 DDR and port. `$01` bits 0-2 = LORAM/HIRAM/CHAREN banking. Power-on `$37`. **Never use.** |
| `$0002-$00FF` | Zero page. BASIC uses `$03-$8F` (CHRGET code lives at `$73-$8A`). KERNAL uses `$90-$FF` (I/O status, jiffy clock `$A0-$A2`, keyboard `$C5/$C6`, screen editor `$D0-$F2`, colour pointers). `$FB-$FE` are officially free. |
| `$0100-$01FF` | Hardware stack (shared with KERNAL/IRQ; our `pha`/`pla` prologues are fine). |
| `$0200-$03FF` | KERNAL/BASIC work area: input buffer `$0200-$0258`, vectors `$0314-$0333` (IRQ/NMI/CHROUT hooks). |
| `$0400-$07FF` | Default text screen (1000 bytes at `$0400`, sprite pointers `$07F8-$07FF`). |
| `$0800` | Must hold `$00`; BASIC program text starts at `$0801`. |
| `$0801-$9FFF` | RAM, ~38 KB usable with BASIC ROM in place. |
| `$A000-$BFFF` | BASIC ROM, RAM underneath (reachable by clearing LORAM: `$01 = $36`). |
| `$C000-$CFFF` | 4 KB free RAM, always visible. |
| `$D000-$DFFF` | I/O (VIC-II `$D000`, SID `$D400`, colour RAM `$D800`, CIA1 `$DC00`, CIA2 `$DD00`), char ROM, or RAM, by `$01`. |
| `$E000-$FFFF` | KERNAL ROM, RAM underneath (clear HIRAM). Vectors at `$FFFA-$FFFF`. |

Bank configurations that matter: `$37` (default: BASIC+KERNAL+IO), `$36` (BASIC out: contiguous RAM
`$0801-$BFFF` ≈ 46 KB, still KERNAL+IO), `$35` (all RAM + IO, no KERNAL: we own IRQ/NMI vectors).

### OS support

There is no OS in the BBC sense; there is the KERNAL, a ROM jump table at `$FF81-$FFF3` reachable with `JSR`
(the table is stable across ROM revisions):

| Entry | Addr | Use for us |
|---|---|---|
| `CHROUT` | `$FFD2` | Write A to the current output (screen). Direct counterpart of `OSASCI`. Treats `$0D` as newline. |
| `CHRIN` | `$FFCF` | From keyboard: runs the screen editor with echo and cursor until RETURN, then hands back the line a char at a time ending in `$0D`. Counterpart of `OSWORD 0`. |
| `GETIN` | `$FFE4` | Non-blocking key (A=0 if none). Useful later for `Console.ReadKey`. |
| `SCNKEY`/`PLOT`/`CLRCHN`/`SETLFS`/`SETNAM`/`LOAD`/`SAVE` | | Not needed in v1; `PLOT` (`$FFF0`) for cursor positioning later. |

- **Text is PETSCII, not ASCII.** In the lower/upper-case set (select with `CHROUT($0E)`; `$8E` selects
  upper/graphics) letters are `$41-$5A` = *lower case*, `$C1-$DA` = *upper case*; ASCII `$61-$7A` shows as
  graphics. `PutChar` must map ASCII→PETSCII (`A-Z` +`$80`, `a-z` −`$20`, `\n`→`$0D`) and `PlatformReadLine` the
  reverse, or `Console.WriteLine("Hello")` will print garbage. The test-emulator stub does the inverse mapping, so
  the `DIFF` lit tests actually verify it.
- **Sound** is direct SID writes (`$D400-$D418`) — no ROM routine. `PlatformBeep` = volume `$D418`, voice-1
  frequency/ADSR/gate, delay loop, gate off.
- **Interrupts:** the KERNAL runs a 50/60 Hz IRQ that scans the keyboard, blinks the cursor and ticks the jiffy
  clock. Leave it on. It preserves A/X/Y and uses only the hardware stack plus KERNAL zero page, so generated code
  is safe as long as it stays out of `$90-$FF` and does not `sei`.
- **Program start:** a `.prg` is `[load addr lo, hi] + bytes`. Auto-start needs a tokenised BASIC line
  `10 SYS 2061` at `$0801` (12 bytes: `0B 08 0A 00 9E 32 30 36 31 00 00 00`), so machine code begins at `$080D`
  = 2061. `PackageImage(code, origin)` computes the number from `origin`.
- **Exit:** we never return to BASIC (its zero page is trampled: CHRGET at `$73-$8A`, pointers at `$2B-$38`).
  The program halts when `Main` returns. See decision 6.

### Web emulators (checked 2026-10-15 with `gh api` unless noted)

| Option | License | State | Notes |
|---|---|---|---|
| `floooh/chips` `systems/c64.h` (+ `c1541.h`) | Zlib | pushed 2026-09-26 | Header-only C, builds to WASM; `c64.h` ~42 KB. **Same recommendation as the ZX plan's fallback — one wrapper approach could serve both.** We write the JS wrapper (framebuffer, audio, keys, `load PRG`, `reset`). ROMs are not part of the library; we must supply them. |
| `nippur72/c64-emu` | **none stated** | pushed 2026-06-07 | Built on chips, TS/Vite, has `load()`/drag-drop PRG and reset. Without a licence we cannot vendor it; could ask the author, otherwise use only as a reference for wrapping chips. |
| `jenoksen/c64-emulator` | GPL-2.0 | created 2026-10-10 (4 days old) | Single HTML file, claims cycle-exact 6510/VIC-II/SID/1541. Brand new, unproven, GPL, single-file → hard to drive. Not a base. |
| `mborgbrant/c64js` | MIT | last push 2022; README: VIC-II "isn't fully emulated" | Too incomplete. |
| `luxocrates/viciious` | none stated | last push 2023 | Single HTML, tinkering-oriented; no licence. |
| VICE → WASM (`vice.js` 2.4, Internet Archive's 3.2 build) | GPL | old/unmaintained ports (web search only, not verified) | Most accurate, but heavy and no maintained embeddable build found. |

**Recommendation:** chips `c64.h` compiled to WASM with our own thin wrapper, exposing the same surface as
`BbcMicroMachine` (`boot(image)`, `run`, `pause`, `isRunning`, `keyDown`, `keyUp`, `clearKeys`). Zlib is the
friendliest licence, it is actively maintained and the machine is complete enough (VIC-II, SID, CIA, keyboard).

**ROMs are the real playground risk** (applies to every option): KERNAL, BASIC and character ROMs are
copyrighted (now Cloanto). The only open alternative seen is `mega65/open-roms` (pushed 2026-05-16, GitHub reports
`NOASSERTION` for the licence — read its LICENSE before use; also check KERNAL jump-table fidelity for the three
calls we use). Fallback: user-supplied ROMs loaded into the panel (IndexedDB), which keeps us clear of
redistribution. Decide before Stage 5 ships, not before it starts.

## Decisions

1. **Image format: `.prg`** with the BASIC `SYS` stub; `DefaultOrigin = 0x080D`. `.d64`/`.t64` later if wanted.
2. **Stay on the default bank (`$01 = $37`) in v1.** KERNAL stays available (`CHROUT`/`CHRIN`), BASIC ROM stays
   mapped, program + heap live in `$080D-$9FFF` (~38 KB). `$36` (extra 8 KB, BASIC unreachable) is a later
   opt-in once nothing needs BASIC.
3. **Zero page: `ZeroPageBase = $02`.** RC0..RC31 = `$02-$21`. Static-frame window stays `$70-$8F`, exactly as
   on the BBC, so `MOS6502FramePlacementPass` can be shared; just parameterise (start, size) on the subtarget.
   Everything below `$90` is free because we never call BASIC; nothing may touch `$90-$FF`.
4. **Heap: per-target start/end.** `ManagedHeap` gets `partial` platform constants (`ManagedHeap.C64.cs` /
   `ManagedHeap.BbcMicro.cs`), or the encoder exposes an `__image_end` symbol so the heap begins right after
   code+data and runs to `$9FFF`. Prefer the symbol (no hand-maintained address); needs a small encoder feature,
   check how `.bss` is placed first. Add an out-of-memory check while here (currently none).
5. **Console I/O through `CHROUT`/`CHRIN` with ASCII↔PETSCII in the runtime.** `\n` is the `$0D` newline.
6. **Exit behaviour: every system halts when `Main` returns; nothing returns to a host.** `__entry` ends in a
   self-branch block (`cf.br` to itself, i.e. `jmp *`) instead of `core.return`. That is target-neutral MIR, so it
   needs no hook and is the same on the BBC Micro, C64 and future machines. Rationale: we will target many 8-bit
   computers and eventually cartridge consoles with nothing to return to, and period software generally took over
   the machine and required a reset to leave (checked 2026-10-15: C64 games rarely exit cleanly; BBC games
   routinely disabled BREAK/ESCAPE with `*FX 200`; consoles have no prompt; the console half is from general
   knowledge, not a source). A later `Environment.Exit`/idle-`halt` form can be added per target (e.g. a console
   `halt` instruction, or a WAI-style low-power wait) without changing this default.
   **This changes existing BBC behaviour** (today `__entry` ends in `rts`) and the test harness, which currently
   detects completion when `__entry` returns to a `$FF03` marker: see Stage 0 and Stage 2.
7. **Reuse `Irie.Tools.Reference`/lit goldens only where C64-specific** (PackageImage, zp base); chip-level goldens
   are already covered by `mos6502`.

## Stages

Each stage ends green under `dotnet test --solution src` in Debug and Release.

### Stage 0 — Make the target a parameter (shared with the ZX plan)

- [ ] `TargetDescriptor` gains `CoreLibId`; `HostAssemblyResolver` static for `c64`.
- [ ] `CompilerDriver.Compile(path, entry)` + `IlToMirTranslator` file ctor take a target id (default `bbcmicro`);
      `dnrc --target <id>`; `iriec --target mos6502-c64`; clear error for an unknown id (test).
- [ ] `Target` hook for the start-up prologue; move the MODE 7 code out of `BuildEntryFunction` into BBC.
- [ ] `BuildEntryFunction` ends in a halt (self-branch block) instead of `core.return` (decision 6), for all
      targets. Make sure no pass deletes or mis-handles the infinite loop (ReturnMerge, block placement,
      tail-dup, DCE). Regenerate any golden that contains `__entry` with `dotlit --regenerate`, review the diff.
- [ ] BBC test harness (`BbcMicroTargetSystem`): completion currently = PC reaches the `$FF03` marker after
      `__entry` returns. Replace with halt detection: at an opcode fetch (`pins.Sync`), if the instruction is
      `JMP abs` whose operand equals its own address, return `Completed`. Keep the tick-budget runaway guard.
      Run the whole `.cs` lit suite to confirm nothing relied on the return.
- [ ] Playground: nothing to change (the emulator just keeps running), but check the BBC panel does not treat
      "program ended" specially.
- [ ] Split `runtime.irie` into `MOS6502/runtime.irie` (shared) + `MOS6502/runtime.bbcmicro.irie`;
      `Irie.csproj` embeds both; `GetRuntime()` concatenates shared + the subtarget's. Existing lit goldens must not
      change (verify with the unchanged test suite).
- [ ] `Target.ZeroPageBase` (default 0) used by `MOS6502MachineCodeEmitter`; test with a synthetic subtarget.
- [ ] Parameterise `MOS6502FramePlacementPass` window (start, size) via the subtarget.
- [ ] `ManagedHeap` start/end becomes per target (decision 4).

### Stage 1 — C64 target class and packaging

- [ ] `MOS6502C64Target : MOS6502Target`: `DefaultOrigin = 0x080D`, `ZeroPageBase = 2`, zp frame placement,
      `GetRuntime()` adds the C64 half, `PackageImage` → `C64PrgImage.Build(code, origin)`.
- [ ] `C64PrgImage` (internal, like `BbcMicroDfsImage`): load address `$0801`, BASIC line `SYS <origin>`, code.
      Unit tests: header bytes, SYS digits for several origins, size.
- [ ] `TargetRegistry`: `C64Id = "c64"`, display name "Commodore 64", `EmulatorKind: "chips-c64"`, same encoder /
      assembly writer as the BBC descriptor; `iriec --target mos6502-c64`.
- [ ] `.irie` lit tests: `IntegerAdd32-Prg.irie` (byte-exact `.prg`), zp-base test (`sta $02` for RC0, nothing
      emitted to `$00/$01`), frame window.

### Stage 2 — Test emulator system

- [ ] `DotNetro.Compiler.Tests.Emulator/TargetSystems/C64TargetSystem.cs`: load at `$080D`, reset vector → `JSR
      $080D`, stub `$FFD2` (record PETSCII→ASCII output; `$0D` → `\n`; ignore `$0E`/`$8E`/`$93`), `$FFCF`
      and `$FFE4` (feed `--input`, ASCII→PETSCII), completion = halt detection (self-`JMP`), shared helper with the BBC system. The Aemula core is a plain NMOS 6502, which is
      all we need; do not emulate the port at `$01` (tests must simply never touch `$00/$01`: add a trap that
      fails the run if they do, as it would be a codegen bug).
- [ ] `Program.cs`: `--target-system c64`; update the option description.

### Stage 3 — CoreLib and runtime for the C64

- [ ] `DotNetro.CoreLib.C64` project (`CoreLibTarget` = `C64`), `Console.C64.cs` with the same four `InternalCall`
      externs as `Console.BbcMicro.cs`, added to the solution and as a content reference of `dnrc`/tests/playground.
- [ ] `MOS6502/runtime.c64.irie`: `PutChar` (ASCII→PETSCII then `jsr $FFD2`), `PlatformReadLine` (`jsr $FFCF` loop
      into a static 80-byte buffer until `$0D`, PETSCII→ASCII, NUL-terminate, return pointer),
      `PlatformBeep` (SID), plus any `oswrch`-style helper the shared `PlatformWriteLineInt32` uses.
- [ ] `ManagedHeap.C64.cs` (or `__image_end`) per decision 4.
- [ ] Start-up prologue (hook from Stage 0): `CHROUT($0E)` for the lower/upper charset, `CHROUT($93)` clear screen;
      optionally set border/background.
- [ ] Nothing C64-specific for exit: the halt comes from `__entry` (decision 6, done in Stage 0).
- [ ] Port the `.cs` lit corpus: add a `RUN-emulated-c64` line (`@dnrc --target c64 --emit program | @emulator
      --target-system c64`) and `DIFF: dotnet emulated-c64` to every test that is not BBC-specific. Tests that
      pin BBC behaviour (e.g. `ReadAndWriteLine` input handling) stay BBC-only unless they port cleanly.

### Stage 4 — Real-machine sanity (not automated)

- [ ] Run the produced `.prg` in a known-good emulator (VICE, desktop; or the playground machine once it exists)
      for: hello world, ReadLine round trip, beep, a long program (checks no `$00/$01`/`$90+` clobber and the
      IRQ keeps ticking), and the halt loop at program end. Record results here.

### Stage 5 — Playground integration

- [ ] Build `c64.h` from chips to WASM (Emscripten or clang-only, as in the 8bitworkshop write-up) with exports:
      init, tick/run-frame, framebuffer, audio, key down/up, reset, quickload PRG, set ROMs. If the ZX plan's chips
      fallback is also built, share the build tooling.
- [ ] Settle the ROM question (open-roms vs user-supplied) and record it in the repo (`NOTICE`/docs).
- [ ] `panels/c64Machine.ts` implementing the `BbcMicroMachine` surface; generalise `emulatorPanel.ts` or add a
      sibling; extend `EMULATOR_PANELS` with `chips-c64`.
- [ ] Keyboard mapping (C64 matrix, positional vs symbolic, RUN/STOP and the `£`/`←` keys).
- [ ] Target picker gets the C64 for free from `TargetRegistry`; add a sample program.

## Risks / open questions

- **Zero-page collisions are silent.** A stray write to `$00/$01` or `$90-$FF` appears as random crashes or a
  stuck keyboard, not a compile error. The Stage 2 trap on `$00/$01` plus a Stage 4 long-run check are the guard;
  consider a static check in `PackageImage`/encoder that rejects zp addresses outside the allowed set.
- **Soft stack pointer (RC0/RC1) is uninitialised today** — see finding 8; needs an answer before long argument
  lists run on any real target.
- **Code size growth.** The default window is `$080D-$9FFF`; the existing corpus is tiny, but string constants and
  the 32-bit/64-bit runtime helpers grow it. Add a clear "program does not fit" error in the encoder (the BBC path
  has none that I saw).
- **PAL vs NTSC** only changes timing; ignore for correctness.
- **VIC-II/graphics** (sprites, bitmap, colour RAM) are out of scope; the `Console` surface is text only. A `Poke`
  style API would be a separate CoreLib plan.
- **ROM licensing** (Stage 5) as above.

## Suggested order

0 → 1 → 2 → 3 (a working compile→emulate loop, headless) → 4 → 5. Stage 0 is independent and can land first, on
its own, behind unchanged tests. Stages 1-3 are small (a target class, a `.prg` writer, a stub emulator and a
hundred lines of MIR runtime); the playground (Stage 5) is the largest piece because there is no ready-made,
properly licensed embeddable C64 emulator.
