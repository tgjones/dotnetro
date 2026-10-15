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

### Hardcoded to BBC — must change (verified in the repo; items marked *done* were fixed in Stage 0)

1. *(done: `Target.ZeroPageBase`)* **Zero-page address of `RC(n)` is `n`** (`MOS6502MachineCodeEmitter.cs:90-91`: `phys.Id - RC(0)`). RC0/RC1 are
   the CC_MOS soft stack pointer, so they land on `$00/$01`. On the C64 those are the 6510 data-direction
   register and the memory-map port; storing a stack pointer there remaps ROM/RAM/IO and crashes. **This is the
   one real blocker on the codegen side.** Fix: a target hook `Target.ZeroPageBase` (default 0; C64 = `$02`)
   used by the emitter, `MOS6502FramePlacementPass`, and anything else that turns a zp physreg into an address
   (grep for `RC(0)` and `- 12`). With base `$02`, RC0..RC31 = `$02..$21`.
   Stage 0 audit: only `MOS6502MachineCodeEmitter` converted a physreg to an address (now `+ ZeroPageBase`). The one
   literal zp address that depended on the base was the trampoline's `jmp.ind 30` (now `$42` = RC30). Frame-placement
   literals are absolute window addresses and need no base. `MOS6502InstructionSelector`'s `PointerZpLo/Hi = 30/31`
   are physreg ids, not addresses, so they follow the base automatically.
2. *(stale, nothing to do)* `IlToMirTranslator.BuildEntryFunction` hardcodes BBC "MODE 7" (`VDU 22,7` via `@oswrch`). The prologue is already the per-target CoreLib partial `DotNetro.Runtime.Startup.Initialize`
   (`Startup.BbcMicro.cs`); the comment was the stale part. C64 adds `Startup.C64.cs`.
3. *(done except `iriec`)* `CompilerDriver.Compile(path, entry)` (line 16-17) and the `IlToMirTranslator` file ctor (line 73) hardcode
   `HostAssemblyResolver.BbcMicro` / `BbcMicroId`; `dnrc` has no `--target`. `Irie.Tools.Compiler/Program.cs:63`
   lists `mos6502-bbcmicro` by name. `Irie.csproj:8` embeds the single `runtime.irie`.
4. *(stale)* `runtime.irie` is one file holding both chip-generic functions (`__call_indirect_trampoline`, mul/div libcalls,
   `StringFromPointer`, `StringBytes`) and BBC OS wrappers (`oswrch`, `PutChar`, `osword`, `PlatformReadLine`,
   `PlatformBeep`, `PlatformWriteLineInt32` — the last calls `PutChar` and is OS-independent in spirit). It must be
   split into a shared 6502 part and a per-system part; `GetRuntime()` returns the concatenation.
   **Correction (Stage 0):** `runtime.irie` only contains `__call_indirect_trampoline`. The BBC OS wrappers are
   CoreLib C# (`Console.BbcMicro.cs`, calling ROM addresses through `delegate* unmanaged[BbcMicroOS]`); the
   mul/div helpers are `MathHelper.cs`. `GetRuntime()` now supports an optional per-subtarget half
   (`MOS6502Target.SystemRuntimeResource`, null), but nothing needs it yet.
5. *(partly done)* `ManagedHeap.s_heapPtr = (byte*)0x1000` is a BBC-specific constant in shared CoreLib code. On the C64 `$1000`
   is inside the program. The heap start/end must be per target (see Memory). Stage 0 added `partial HeapStart()` per target; the end limit / OOM check remains.
6. *(open, Stage 2/3)* The `.cs` lit tests' RUN line is `@dnrc --emit program | @emulator --target-system bbcmicro`; C64 needs
   `--target c64` (`dnrc --target` exists now) on `dnrc` and `--target-system c64` on the emulator.
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
| `@emulatorjs/core-vice_x64` 4.2.3 (VICE libretro core built by EmulatorJS) | core GPL-2.0, frontend `@emulatorjs/emulatorjs` GPL-3.0 | npm package built 2025-06 (frontend repo active) | **Investigated 2026-10-15, not yet run.** The package is only four `.data` files, each a 7z of one Emscripten build (`vice_x64_libretro.js` + `.wasm`, 6.7 MB; variants: plain, legacy, thread, thread-legacy). It is a whole RetroArch build, not a library: no documented embedding API, the API is the EmulatorJS frontend. **The wasm embeds the C64 ROMs** (KERNAL 901227, BASIC, chargen found at fixed offsets), so no separate ROM hosting. See "EmulatorJS core" below. |
| VICE → WASM (`vice.js` 2.4, Internet Archive's 3.2 build) | GPL | old/unmaintained ports (web search only, not verified) | Most accurate, but heavy and no maintained embeddable build found. |

**Recommendation:** chips `c64.h` compiled to WASM with our own thin wrapper, exposing the same surface as
`BbcMicroMachine` (`boot(image)`, `run`, `pause`, `isRunning`, `keyDown`, `keyUp`, `clearKeys`). Zlib is the
friendliest licence, it is actively maintained and the machine is complete enough (VIC-II, SID, CIA, keyboard).

#### EmulatorJS core (investigated 2026-10-15; nothing run yet)

- **Shape:** `vice_x64_libretro.js` defines a global `EJS_Runtime`, an Emscripten factory returning a `Module` for a
  RetroArch/libretro build. Rather than use the 6k-line GPL-3 frontend, we can do what its `GameManager.js` does:
  `EJS_Runtime({ noInitialRun: true, canvas, locateFile })`, write a `retroarch.cfg` and the `.prg` into `Module.FS`,
  `Module.callMain(['/game.prg'])`, `resumeMainLoop()`. Exported C helpers (via `cwrap`): `system_restart`,
  `simulate_input(player, index, value)`, `ejs_set_variable` (core options), `toggleMainLoop`, `load_state`,
  `get_video_dimensions`, `ejs_set_keyboard_enabled`.
- **Hosting:** use the non-thread wasm (the thread builds need `SharedArrayBuffer`, i.e. COOP/COEP headers that GitHub
  Pages cannot send). Extract the 7z at build time (`7z`/`bsdtar`; both on `ubuntu-latest` or via an npm 7z package)
  and serve `.js` + `.wasm` directly. Add the npm package as a build-time dependency copied into `dist`, so neither the
  core nor the ROMs it embeds are committed (same stance as the BBC ROMs, see `THIRD-PARTY-NOTICES.md`).
- **Licence:** GPL-2.0 core served as an unmodified separate file next to the MIT playground (as jsbeeb is GPL-3);
  add it to `THIRD-PARTY-NOTICES.md` with a source link. The embedded ROMs are Commodore/Cloanto copyright (accepted
  for publication by the repo owner, 2026-10-15).
- **Spike results (2026-10-15, headless Chromium via Playwright, a plain page with no frontend; page and driver were
  throwaway in `/tmp/c64-spike`):**
  - Loading works as sketched above (`EJS_Runtime(...)`, `retroarch.cfg` + `.prg` into `FS`, `callMain`,
    `resumeMainLoop`). One extra: `Module.callbacks.setupCoreSettingFile = path => { mkdir -p its parent; FS.writeFile(path, '') }`
    must create the directory, or the core throws an `ErrnoError`.
  - The `.prg` autostarts through the SYS stub: first frame after ~1 s, our output on screen by ~4.5 s (software GL).
    The test program (mixed case, negative int, ReadLine, halt) renders correctly.
  - **Running another program: write the new bytes over `/game.prg` and call `system_restart`.** It reset the machine and
    ran the second program, so no module teardown is needed.
  - **Keyboard** (`ejs_set_keyboard_enabled(1)`, canvas clicked): letters, shifted letters, space and RETURN reach
    `Console.ReadLine` correctly. Quirk: with the default `vice_keyboard_keymap = positional`, Shift+digit gives the digit
    (Shift+1 = `1`, not `!`). Switching with `ejs_set_variable('vice_keyboard_keymap','symbolic')` fixed Shift+2 (`@`)
    but Shift+1 still gave `1` in one try; needs more work (other options: `vice_physical_keyboard_pass_through`, or
    our own DOM-key → `simulate_input` mapping). RUN/STOP, `£`, `←` untested.
  - Not tested: audio (SID beep; needs a user gesture), PAL border/canvas sizing in the panel, a module created while
    the panel is hidden.
  - The "Built for EmulatorJS … Download a copy from github.com/EmulatorJS/EmulatorJS … View the licence here" text is
    three lines the wasm sends to `Module.print` at start-up. It is never drawn on the canvas; it only showed up in the
    spike because the page appended `print` output to the DOM. Pass `print`/`printErr` handlers that ignore it (or log
    to the console) and nothing EmulatorJS-branded appears in the panel. (The licence notice then belongs in
    `THIRD-PARTY-NOTICES.md`.)
- **Versus `chips` `c64.h`:** the VICE core is far more accurate and brings ROMs and sound with it, but it is a
  RetroArch build that we control only through a thin C surface; `c64.h` would give a clean API but needs our own
  wrapper, audio, input and separately hosted ROMs. Recommendation after the spike: use the EmulatorJS core; the
  restart path works and the keyboard is mostly there. Fall back to `chips` only if the keyboard quirks cannot be tamed.

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

### Stage 0 — Make the target a parameter (shared with the ZX plan) — DONE

- [x] `TargetDescriptor` gains `CoreLibId`; `HostAssemblyResolver.ForTarget(id)` replaces the static `BbcMicro`
      (the c64 corelib itself arrives in Stage 3).
- [x] `CompilerDriver.Compile(path, entry, targetId)` + `IlToMirTranslator` file ctor take a target id (default
      `bbcmicro`); `dnrc --target <id>`; unknown id → `ArgumentException` (test).
      **Deferred to Stage 1:** `iriec --target mos6502-c64` (needs the `MOS6502C64Target` class).
- [x] Start-up prologue hook: *already exists* — `DotNetro.Runtime.Startup.Initialize` is a per-target CoreLib partial
      (`Startup.BbcMicro.cs` holds the MODE 7 code), so Stage 3 only needs `Startup.C64.cs`. Finding 2 was stale.
- [x] `__entry` ends in a self-branch halt block for all targets (`BuildEntryFunction`). No pass mishandled it; no
      golden contained `__entry`.
- [x] BBC test harness: completion = `JMP *` detected at opcode fetch (`HaltDetection.IsHalt`, shared helper in
      `ITargetSystem.cs`); the `$FF03` marker is gone. Full `.cs` lit suite passes; the tick budget still guards runaways.
- [x] Playground: no panel code treats program end specially (nothing to change).
- [x] `GetRuntime()` = shared `runtime.irie` + optional subtarget half (`MOS6502Target.SystemRuntimeResource`, null
      today). Finding 4 was stale: `runtime.irie` only held the trampoline, the BBC OS wrappers are CoreLib C#
      (`Console.BbcMicro.cs`), so there is no BBC half to split out. Stage 3's `runtime.c64.irie` may not be needed
      either (PutChar etc. can be CoreLib C# too); revisit there. (Embedding a second resource in `Irie.csproj` is
      deferred until a half exists.)
- [x] `Target.ZeroPageBase` (default 0) used by `MOS6502MachineCodeEmitter(zeroPageBase)`; `ZeroPageBaseTests` uses a
      synthetic subtarget. The trampoline's hard-coded `jmp.ind 30` literal would have ignored the base, so it is now
      `jmp.ind $42` (RC30, with `[liveins: $42]`) and the emitter accepts an RC register there.
- [x] `MOS6502FramePlacementPass(windowStart = 0x70, windowSize = 0x20)` takes its window from the subtarget.
- [x] `ManagedHeap` start is per target via `partial HeapStart()` (`ManagedHeap.BbcMicro.cs`). The `__image_end`
      symbol and an out-of-memory check from decision 4 are still open (Stage 3).

### Stage 1 — C64 target class and packaging — DONE

- [x] `MOS6502C64Target : MOS6502Target`: `DefaultOrigin = 0x080D`, `ZeroPageBase = 2`, zp frame placement (`AddPostRegisterAllocationPasses` adds `new MOS6502FramePlacementPass(0x70, 0x20)`; the pass
      already takes its window),
      `PackageImage` → `C64PrgImage.Build(code, origin)`.
- [x] `C64PrgImage` (internal, like `BbcMicroDfsImage`): load address `$0801`, BASIC line `SYS <origin>`, code.
      Unit tests: header bytes, SYS digits for several origins, size.
- [x] `TargetRegistry`: `C64Id = "c64"`, display name "Commodore 64", `EmulatorKind: "vice-c64"`, same encoder /
      assembly writer as the BBC descriptor, `CoreLibId = "c64"`; add `mos6502-c64` to `iriec`'s target switch
      (`Irie.Tools.Compiler/Program.cs`, deferred from Stage 0).
- [x] `.irie` lit tests: `IntegerAdd32-Prg.irie` (byte-exact `.prg`), zp-base test (`sta $02` for RC0, nothing
      emitted to `$00/$01`), frame window.

Notes: `C64PrgImage` throws if the origin overlaps the BASIC stub or the program runs past `$FFFF` (the `$9FFF` ROM
limit is not checked; see Risks). Registering `c64` makes the playground list "Commodore 64" before Stage 3/5: its
compile fails with an "Internal compiler error" (no `DotNetro.CoreLib.c64` resource) and no emulator panel exists for
`vice-c64` (now renamed from `chips-c64`; Stage 5 added the panel).

### Stage 2 — Test emulator system — DONE

- [x] `DotNetro.Compiler.Tests.Emulator/TargetSystems/C64TargetSystem.cs`: load at `$080D`, reset vector → `JSR
      $080D`, stub `$FFD2` (record PETSCII→ASCII output; `$0D` → `\n`; ignore `$0E`/`$8E`/`$93`), `$FFCF`
      and `$FFE4` (feed `--input`, ASCII→PETSCII), completion = halt detection via the existing `HaltDetection.IsHalt` helper (`ITargetSystem.cs`), called at `pins.Sync`
      exactly as `BbcMicroTargetSystem` does. The BBC harness no longer needs a return marker; the C64 reset stub is just
      `JSR $080D`. The Aemula core is a plain NMOS 6502, which is
      all we need; do not emulate the port at `$01` (tests must simply never touch `$00/$01`: add a trap that
      fails the run if they do, as it would be a codegen bug).
- [x] `Program.cs`: `--target-system c64`; update the option description.

Notes: `$FFCF` hands back a line a character at a time ending in `$0D`, like the screen editor; `$FFE4` returns 0
when no characters are pending (it never reads a new line). Unmapped PETSCII output shows as `?` so stray bytes are
visible in a DIFF. The `$00/$01` trap arms at the first opcode fetch (the reset sequence touches low addresses) and
ends the run with `EmulationStatus.Fault` (exit code 4, message on stderr). Covered by two hand-assembled lit tests in
`DotNetro.Compiler.Tests/Lit/Emulator/` (`printf` octal escapes, as CI's `/bin/sh` is dash).

### Stage 3 — CoreLib and runtime for the C64 — DONE

- [x] `DotNetro.CoreLib.C64` project (in the solution, copied beside `dnrc` and the tests as `corelib/c64/`, embedded in
      the playground as `DotNetro.CoreLib.c64`), with `Console.C64.cs` and `Startup.C64.cs`.
- [x] KERNAL calls are CoreLib C# through `delegate* unmanaged[C64Kernal]` at `$FFD2` (CHROUT) and `$FFCF` (CHRIN). New
      calling convention `CallConvention.C64Kernal` (`c64_kernal`, marker type `CallConvC64Kernal`); its lowering is
      the BBC ROM one, now shared as `MOS6502RomCallLowering` (A/X/Y in, A/X/Y/flags clobbered, no RC clobbered).
      `PutChar` converts ASCII→PETSCII (swap letter case); `PlatformReadLine` loops CHRIN into the tape buffer at
      `$033C` (max 80 chars, PETSCII→ASCII, ends in CR); `PlatformBeep` writes SID voice 1 directly and busy-waits.
      Note `starg` is not supported by the translator, so these use locals instead of assigning to parameters.
- [x] Heap: `ManagedHeap` has per-target `HeapStart()` / `HeapEnd()`. The C64 start is the encoder-defined symbol
      `__image_end` (`MOS6502BinaryEncoder.ImageEndSymbol`, the address past the last image byte) read by
      `@HeapStart` in the new `runtime.c64.irie` (the first user of `SystemRuntimeResource`), which backs an
      `InternalCall` partial; the end is `$A000`. `Alloc` now halts (a `while (true)` loop) when out of memory, on
      every target; BBC end is `$7C00`.
- [x] Start-up: `CHROUT($0E)` (lower/upper-case set) then `CHROUT($93)` (clear screen); colours left at the defaults.
- [x] Exit: halt from `__entry` (Stage 0).
- [x] Lit corpus: every `.cs` test now has `RUN-emulated-c64` and `DIFF: dotnet emulated-c64` (all 103+ pass,
      including `ReadAndWriteLine`). None needed to stay BBC-only.

Notes: the OOM halt is silent (the test harness reports `Completed` with truncated output, which a DIFF catches); a
visible failure needs an output path. The soft stack pointer (finding 8) was not investigated here.

### Stage 4 — Real-machine sanity (not automated) — partly done

- [~] Run the produced `.prg` in a known-good emulator (VICE, desktop; or the playground machine once it exists)
      for: hello world, ReadLine round trip, beep, a long program (checks no `$00/$01`/`$90+` clobber and the
      IRQ keeps ticking), and the halt loop at program end. Record results here.

Result (2026-10-15, run by Tim in an emulator): a small program (hello with mixed case, negative int, ReadLine round trip,
beep, halt) works as expected. Not yet exercised: a long program (zero-page/`$90+` clobber, IRQ ticking over a long run).

### Stage 5 — Playground integration — DONE (core: EmulatorJS VICE; see the spike notes above)

- [x] Emulator: the EmulatorJS `vice_x64` libretro core instead of building `c64.h` from `chips` (spike results above).
      `@emulatorjs/core-vice_x64` and `7z-wasm` are devDependencies; `scripts/c64Core.ts` unpacks the plain wasm build
      once into `node_modules/.cache/c64-core/`, which the Vite dev server serves under `c64/` and the build copies
      into `dist/c64/`. Neither the core nor the ROMs embedded in it are in the repo.
- [x] ROMs: nothing to settle in the repo; they ship inside the core's wasm. Accepted for publication by the repo
      owner (2026-10-15); recorded in `THIRD-PARTY-NOTICES.md` with the core's GPL notice.
- [x] `panels/c64Machine.ts` implements a new `Machine` interface (`panels/machine.ts`: boot/run/pause/bindInput/dispose);
      `BbcMicroMachine` implements it too. `emulatorPanel.ts` is now an abstract `EmulatorPanel` with
      `BbcMicroEmulatorPanel` / `C64EmulatorPanel` subclasses; `EMULATOR_PANELS` maps `vice-c64` → `emulator-c64`.
      A panel ignores images compiled for another system's target. The core's `print` output (EmulatorJS banner) is dropped.
- [x] Keyboard: the core binds key events itself; the panel enables it (`ejs_set_keyboard_enabled`) only while the canvas has
      focus. **Known quirk, not fixed:** the default positional mapping gives digits for Shift+digit (see spike notes).
- [x] Target picker shows "Commodore 64" from `TargetRegistry`; the download link now takes its name from the target
      (`TargetDescriptor.ImageFileName`: `MAIN.ssd` / `program.prg`). Canvas is letterboxed to 4:3 by `C64Machine`.
- [ ] Not done: a C64 sample program (the default source is shared), audio verified in a real browser, RUN/STOP and the
      `£`/`←` keys, Shift+digit symbols.
- Verified headlessly (Playwright + Chromium, software GL): select the C64 target, add an Emulator panel from the `+`
  menu, the program runs; a second compile restarts it; typing into the panel reaches `Console.ReadLine`; `vite dev`
  and `vite build` both serve the core.

## Risks / open questions

- **Zero-page collisions are silent.** A stray write to `$00/$01` or `$90-$FF` appears as random crashes or a
  stuck keyboard, not a compile error. The Stage 2 trap on `$00/$01` plus a Stage 4 long-run check are the guard;
  consider a static check in `PackageImage`/encoder that rejects zp addresses outside the allowed set.
- **Soft stack pointer (RC0/RC1) is uninitialised today** — see finding 8 (still unverified after Stage 0); needs an answer before long argument
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
