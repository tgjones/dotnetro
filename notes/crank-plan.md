# Crank: cross-platform game engine plan

Goal: write a 2D game once in C# and ship it on the BBC Micro, Commodore 64, NES and ZX Spectrum (later, maybe, the
Atari 2600), with the game logic shared and the presentation and tuning per platform. The engine, **Crank**, is a layer
**above** DotNetro: DotNetro stays a compiler (C# → Irie MIR → 6502/Z80) with a core library, and Crank is ordinary C#
that the compiler compiles.

The kind of game in scope is what these machines are known for: single-screen and flip-screen platformers, top-down
adventures, maze and arcade games, and (later) side-scrollers.

## Decisions (agreed)

- **Shared logic, per-platform presentation and tuning.** A game is one code base with per-platform parts where it needs
  them (art, layout, speed, sprite counts). Where the engine can take care of a per-platform concern entirely, it gets a
  cross-platform API that is a no-op on the platforms that don't need it (for example a sprite-multiplexer hint that only
  the C64 uses). Those are decided case by case, as they come up.
- **NES is a must.** It is also the platform that changes the compiler most (code in ROM, 2K RAM, video updates only in
  vertical blank), so it comes early, not last.
- **Free and open-source tools only.** The content pipeline reads open file formats, so any FOSS tool that writes them
  works, and nothing requires a proprietary tool.
- **The engine lives in this repo**, beside the compiler and the core library.
- **Names.** DotNetro keeps its name for the compiler and the project as a whole; Irie stays the backend; the engine is
  **Crank** (old engines were started with a hand crank; "crank it up"). Projects, namespaces and packages are `Crank`,
  `Crank.<Target>`, `Crank.Content` and so on.

## Open questions

- **Who owns the main loop.** An XNA-style framework (`Update`/`Draw` called by the engine) or a library the game calls
  from its own `while (true) { Frame.Wait(); … }`. To be decided from the first engine slice (stage 3), not up front.
- **The demo game.** Proposed shape below; theme and art are open.
- **Atari 2600.** In or out, and if in, as a restricted profile (see [Platforms](#platforms)).
- **Machine variants.** BBC Model B only, or also the Master (shadow RAM)? Spectrum 48K or 128K (AY sound, banked RAM)?
  Which NES mappers (NROM first; later UxROM/MMC1 for more code)?

## Platforms

| | CPU | RAM | Display | Sprites | Scrolling | Sound | Media |
|---|---|---|---|---|---|---|---|
| BBC Micro B | 6502, 2 MHz | 32K, shared with the screen | bitmap; MODE 2 20K, MODE 5 10K, MODE 7 1K (teletext) | none: software | coarse, by moving the CRTC screen start | SN76489 (3 tone + noise) | disc (`.ssd`), tape |
| Commodore 64 | 6510, ~1 MHz | 64K | characters or bitmap (VIC-II) | 8 in hardware, reusable down the screen with raster interrupts | fine, in hardware | SID | `.prg`, `.d64`, `.crt` |
| ZX Spectrum | **Z80**, 3.5 MHz | 48K (128K on later models) | bitmap + 8×8 colour attributes | none: software | none | beeper (AY on the 128K) | `.tap`, `.sna` |
| NES | 2A03 (6502, no decimal mode), ~1.79 MHz | **2K**, code in cartridge ROM | tiles (PPU), video memory writable only in vertical blank | 64 in hardware, 8 per scanline | fine, in hardware | APU (2 pulse, triangle, noise, DPCM) | iNES `.nes` |
| Atari 2600 | 6507, 1.19 MHz | **128 bytes**, 4K ROM (banked for more) | no frame buffer: the CPU drives each scanline | 2 players, 2 missiles, ball, playfield | none | TIA | `.a26` |

Consequences for the engine design:

- **A tiles-plus-sprites model** fits all four main targets: natively on the NES, nearly natively on the C64 (character
  modes + hardware sprites), and with software rendering on the BBC Micro and Spectrum (pre-shifted, masked sprites
  drawn over a tile map).
- **Abstract over intent, not mechanism.** "Put sprite 3 at (x, y)" and "set tile (5, 2)" are cheap everywhere; a
  "set pixel" API is cheap nowhere and is not offered.
- **The NES shapes the display API.** Video memory can only be written during vertical blank, so tile and palette
  changes go through an update queue that the vertical-blank interrupt (NMI) drains. The other platforms can apply the
  queue immediately or at their own vertical blank; the API is the same.
- **The 2600 does not fit the general engine.** With 128 bytes of RAM and cycle-counted display code, the C# code can
  only run in the vertical blank and overscan periods, while the engine supplies a few fixed display routines
  ("kernels", as batari Basic does). If we do it, it is a separate profile that shares input, sound and game logic but
  not the display API.
- **The Spectrum needs the Z80 backend first** ([zx-spectrum-plan.md](zx-spectrum-plan.md)).
- **Memory on the BBC Micro is the tightest of the four.** `DefaultOrigin` is `0x2000` and MODE 2 / MODE 1 screen
  memory starts at `0x3000`, leaving 4K for the program; MODE 5 (from `0x5800`) leaves about 14K. The engine needs
  smaller custom screen modes (fewer CRTC columns or rows, as many BBC games did) and the compiler a lower origin and a
  heap that fits around them. Code density matters on every target, but it limits the BBC first.

## Architecture

Three layers:

1. **DotNetro + CoreLib** (exists): language features, the per-target core library, and the low-level hooks the layers
   above need (see [Compiler prerequisites](#compiler-prerequisites)).
2. **Platform libraries**, one per system: thin, honest wrappers over the hardware (VIC-II, SID and CIA registers on the
   C64; the CRTC, video ULA, SN76489 and OS calls on the BBC Micro; PPU and APU on the NES). Useful on their own, and the
   escape hatch when the engine does not fit a game.
3. **The engine**: the portable API, implemented per platform on top of layer 2.

Proposed layout, following the CoreLib's pattern (shared sources compiled once per target by a per-target project,
`Foo.cs` the shared surface and `Foo.<Target>.cs` the platform half through `partial` methods):

```
src/Crank/                      shared engine sources (no project), Crank.props / Crank.targets
src/Crank.BbcMicro/             builds Crank for the BBC Micro: *.cs + *.BbcMicro.cs
src/Crank.C64/                  … for the C64
src/Crank.Nes/                  … for the NES (stage 5)
src/Crank.Platform.BbcMicro/    layer 2 for the BBC Micro (or a folder inside the engine projects; decide in stage 1)
src/Crank.Platform.C64/
src/Crank.Content/              the content pipeline: MSBuild task + source generator
src/Crank.Samples.<Game>/       the demo game
```

Key rules:

- **The platform is chosen at compile time, never at runtime.** No interfaces or virtual calls in the hot path.
  Capabilities are `const` values (`Platform.HasHardwareSprites`, `Platform.MaxSprites`, `Platform.ScreenWidthTiles`),
  so ConstantBranchFold and UnreachableBlockElimination remove the paths a platform does not take.
- **No managed allocation in the game loop.** Fixed pools of structs, byte or 8.8 fixed-point coordinates, lookup
  tables. The API makes the cheap path the natural one: entity pools rather than `new Enemy()`.
- **Games build like the CoreLib**: the game's project is compiled once per target against that target's engine build.
  How a game project picks its targets (one project per target, or one project with a target list that the build fans
  out) is decided in stage 3.

## Crank API (first cut)

Areas, roughly in the order the demo game needs them:

- **Frame**: wait for vertical blank, frame counter, PAL/NTSC, a frame budget (debug builds can show how much of the frame
  was used, e.g. as a border colour change).
- **Display**: screen setup per platform (custom BBC modes, C64 character mode and memory bank, NES PPU), logical colours
  mapped to each palette, the update queue.
- **Tiles and maps**: tile sets, a tile map, tile attributes (solid, ladder, hazard, …) for collision, a camera. Scrolling
  later: by whole tiles everywhere, smooth where the hardware allows.
- **Sprites / actors**: a declared maximum count; hardware sprites, reused hardware sprites or software sprites per
  target; animation frames.
- **Collision**: actor against tile attributes, actor against actor (bounding boxes).
- **Text and HUD**: a font from the content pipeline, score and lives, BCD scores.
- **Input**: logical buttons (`Left`, `Right`, `Up`, `Down`, `Fire`, `Start`) mapped to keys on the BBC Micro and
  Spectrum, joystick on the C64, pad on the NES; key redefinition where there is a keyboard.
- **Audio**: a music player and sound effects with priority, driven once per frame.
- **Utilities**: fixed-point maths, small lookup tables (sine), an LFSR random number generator.
- **Storage** (later): high scores and level loading where the machine has a disc or tape; none on the NES and 2600.

The example below is only to give the flavour; the real shape comes out of stage 3:

```csharp
while (true)
{
    Frame.Wait();
    var pad = Input.Read(Player.One);
    if (pad.Held(Button.Left)) hero.X -= 1;
    hero.Move(level);          // tile collision from tile attributes
    Sprites.Set(0, Assets.HeroWalk, hero.X, hero.Y);
    Audio.Update();
}
```

## Content pipeline

Much of the per-platform work is in the data, so the pipeline is a first-class part of the engine, not an afterthought.

Inputs (open formats, with the FOSS tools that write them):

| Asset | Format | Tools |
|---|---|---|
| Sprites, tiles, fonts | PNG (indexed or RGB) | LibreSprite, Pixelorama, GIMP |
| Maps | Tiled `.tmx` / `.tmj`; LDtk `.ldtk` | Tiled, LDtk |
| Music, sound effects | Furnace `.fur` | Furnace (supports SID, APU, SN76489, AY and TIA in one tool) |

Outputs, per platform: BBC MODE 2/5 byte layouts and pre-shifted masked sprites; C64 character sets, multicolour sprites
(64-byte aligned) and colour data; NES pattern tables (CHR), palettes and attribute tables; Spectrum 1bpp graphics with
attributes; per-chip music and sound-effect data; maps, compressed (RLE, or ZX0, which has small 6502 and Z80
decompressors).

- **Validation per platform, with clear errors**: "this sprite uses 4 colours; a C64 multicolour sprite allows 3",
  "colour clash in the 8×8 cell at (16, 8) on the Spectrum", "this screen needs 300 unique tiles; the NES has 256".
- **Integration with the build**: MSBuild items in the game project (`<GameAsset Include="art/hero.png" … />`), an
  MSBuild task that converts them per target, and a source generator that gives the game typed handles
  (`Assets.HeroWalk`, `Assets.Level1`). Per-platform overrides by file name (`hero.png`, `hero.Spectrum.png`), like the
  CoreLib's sources.
- **Sound playback** needs one player routine per sound chip, reading data converted from `.fur`. This is one of the
  largest pieces of the whole effort, so it gets its own stage.

## Compiler prerequisites

Found by reading the repo (2026-10-20); each one blocks a stage below.

- **Embedded read-only data.** The usual C# form, `static ReadOnlySpan<byte> Data => new byte[] { … };`, needs
  `ldsflda` (of the compiler-generated RVA field) and array initialisers need `ldtoken` +
  `RuntimeHelpers.InitializeArray`; [il-support.md](../doc/dotnetro/il-support.md) lists `ldsflda` and `ldtoken` as not
  implemented. The data must become read-only module data (in ROM on the NES), not a heap copy.
- **Placement control for globals**: alignment (C64 sprites on 64 bytes, character sets on 2K, NES-style page-aligned
  tables) and fixed addresses or reserved regions (C64 video memory bank, BBC screen memory), so the compiler's layout
  never overlaps what the hardware uses.
- **Code and read-only data in ROM, writable data in RAM**: separate read-only and writable sections, a startup routine
  that zeroes the writable section and copies initialised data, no self-modifying code. The NES needs this; cartridges
  later need bank switching.
- **Interrupt handlers written in C#**: a method marked as an interrupt handler (attribute) gets a prologue and epilogue
  that save and restore the registers and zero-page bytes it uses and returns with `RTI`, and is installed in the vector
  (C64 raster interrupts through `$0314`, BBC IRQ vectors, NES NMI/IRQ vectors in ROM). The `volatile.` prefix is
  already honoured because no pass reorders memory operations (`IlToMirTranslator.cs`, the `ILOpCode.Volatile` case);
  that must stay true, or the flag must be carried on the op.
- **Memory map per target and screen mode**: a lower BBC origin, and a heap whose start and end fit around screen
  memory (see also [gc-plan.md](gc-plan.md), which already notes the heap at `0x1000` running into the program at
  `0x2000`).
- **Zero-page budgets** shared between the engine (fast pointers for blitting, sound player state) and the compiler's
  static frames and imaginary registers.
- **Code density** stays an ongoing priority (the [benchmarks](../doc/dotnetro/benchmarks.md) and
  [hello-world-size-plan.md](hello-world-size-plan.md) work), measured on the engine and demo game once they exist.

## Testing and tooling

- **Crank behaviour in lit tests**: the `.cs` lit tests already run every program on the BBC Micro and C64 in the
  emulator (`RUN-emulated` / `RUN-emulated-c64`, `DotNetro.Compiler.Tests.Emulator/TargetSystems/`). Engine tests that
  print results (collision, maths, input mapping with scripted input) fit that model directly.
- **Frame budgets**: the emulator counts cycles, so tests can assert that a frame's update fits in N cycles on each
  target and catch performance regressions.
- **Screen checks**: compare screen memory (or a rendered image) after N frames with a golden, for the display code.
- **NES and Spectrum systems** in the test emulator and the playground, when those targets arrive.
- **Playground**: a demo-game entry and the engine assemblies embedded, so games run in the browser on every target.

## The demo game

A game that grows in stages, so that each stage tests more of the engine:

1. **Single-screen arcade platformer** (in the style of Manic Miner or Bubble Bobble): collect items, avoid patrolling
   enemies, about 10 levels. Exercises tiles, sprites, tile collision, input, HUD, music and sound effects. Fits all
   four main targets, and could be cut down for the 2600.
2. **Flip-screen rooms**: the same game becomes a connected map (Atic Atac or early Zelda style). Exercises map
   streaming and compression.
3. **One scrolling level** (optional, later): where the platforms differ most, so a good test of the "no-op on other
   platforms" abstraction.

## Stages

### Stage 0: compiler prerequisites for data

- [ ] `ldsflda` for RVA fields and `ldtoken` + `InitializeArray`, so `ReadOnlySpan<byte> X => new byte[] { … }` compiles
      to read-only module data; lit tests on both targets.
- [ ] Alignment and fixed-address or reserved-region placement for globals.
- [ ] BBC memory map: lower origin, heap bounds that respect the chosen screen mode.

### Stage 1: platform libraries for BBC Micro and C64

- [ ] Choose the project layout (separate `Crank.Platform.*` projects or folders inside the engine projects).
- [ ] Typed hardware access: C64 VIC-II, SID, CIA; BBC CRTC, video ULA, SN76489 (through the OS or the system VIA).
- [ ] Wait for vertical blank on both; keyboard and joystick reading.
- [ ] Interrupt handlers in C# (compiler work) and a C64 raster interrupt and BBC vsync handler on top of them.

### Stage 2: content pipeline v0

- [ ] `Crank.Content`: MSBuild items, per-target conversion task, source generator for typed asset handles.
- [ ] PNG → tiles and sprites for BBC (MODE 5 first) and C64 (character mode + multicolour sprites), with validation.
- [ ] Tiled maps → compressed tile maps with tile attributes.
- [ ] A font.

### Stage 3: first engine slice on BBC Micro and C64

- [ ] Frame, display setup (one custom BBC mode, C64 character mode), tile map, sprites (software on the BBC, hardware
      on the C64), input, collision, HUD.
- [ ] Demo game, one level, playable on both.
- [ ] Decide the main-loop question and the game-project shape from this experience; record them here.
- [ ] Frame-budget and screen-check tests.

### Stage 4: audio

- [ ] Furnace `.fur` import in the pipeline.
- [ ] Music and sound-effect players for SN76489 (BBC) and SID (C64), driven once per frame.

### Stage 5: NES

- [ ] Irie/DotNetro: a `nes` target (NROM first): code and read-only data in PRG ROM, writable data in the 2K RAM,
      startup routine, NMI/IRQ/reset vectors, CHR ROM from the pipeline, iNES packaging.
- [ ] An NES system in the test emulator and the playground.
- [ ] Platform library (PPU, APU, pads) and the engine's NES backend with the vertical-blank update queue.
- [ ] Pipeline: CHR, palettes, attribute tables, APU music.
- [ ] Demo game on the NES.

### Stage 6: the demo game grows

- [ ] Ten levels, then flip-screen rooms; map streaming and compression.

### Stage 7: ZX Spectrum

- [ ] Depends on the Z80 backend ([zx-spectrum-plan.md](zx-spectrum-plan.md)).
- [ ] Platform library, engine backend (software sprites, attributes), beeper or AY player, pipeline outputs.

### Stage 8 (optional): Atari 2600 profile

- [ ] Fixed display kernels, game logic in vertical blank and overscan, a cut-down demo game.

### Stage 9 (optional): scrolling

- [ ] Smooth scrolling on C64 and NES, coarse on the BBC Micro, a decision for the Spectrum.

## Risks

- **Code size.** Compiled C# must be dense enough to leave room for a game, especially on the BBC Micro (screen memory)
  and the NES (32K PRG ROM without a mapper, 2K RAM).
- **Software sprites are expensive** on the BBC Micro and Spectrum: the engine must use pre-shifted data, dirty-rectangle
  redraws and small sprite counts there, and the demo game must be designed so that it still plays well.
- **Sound players** for four or five chips are a large amount of specialist work.
- **The abstraction may leak.** If a game needs per-platform code in many places, the API is at the wrong level; the
  demo game is how we find out, which is why the API is designed from it rather than up front.

## Prior art

- **8bit-Unity** (C on cc65): write once, deploy to several 8-bit platforms. <https://github.com/8bit-Dude/8bit-Unity>
- **Cross-Lib / Cross Chase** (Fabrizio Caruso; cc65, z88dk, CMOC): one C API for about 200 targets, with automatic
  graphics conversion. A lowest-common-denominator design, which this plan avoids.
- **MPAGD** (Jonathan Cauldwell): a game-construction kit for the Spectrum, BBC Micro, Atom, CPC, MSX and others; useful
  evidence of a game-object model that ports across these machines.
- **TRSE** (Turbo Rascal): one Pascal-like language for 20+ platforms, with mostly per-platform libraries.
- **llvm-mos SDK** (neslib-style NES support), **batari Basic** (the 2600 kernel model), **z88dk SP1** (Spectrum
  software sprites), **XNA / MonoGame** (the API shape C# game programmers know).
