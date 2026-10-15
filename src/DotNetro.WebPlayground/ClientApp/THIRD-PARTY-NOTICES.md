# Third-party notices

## jsbeeb

The BBC Micro emulator is [jsbeeb](https://github.com/mattgodbolt/jsbeeb) by Matt Godbolt and
contributors, licensed under the GNU General Public License v3.0 or later. Its source is available
at the link above.

## BBC Micro ROMs

The deployed site serves three ROM images taken unmodified from the jsbeeb package
(`public/roms/`): `os.rom` (the Acorn MOS 1.20 operating system), `BASIC.ROM` (BBC BASIC II) and
`b/DFS-1.2.rom` (Acorn DFS 1.20). They are not part of this repository's source and are not covered
by the licences above. They are copyright Acorn Computers Ltd and its successors, and are
distributed here on the same basis as in jsbeeb, whose README says: "The ROMs here are NOT GPL.
They are copyrighted still, but I think it's fair use provided you actually own these ROMs. I would
be happy to remove these if anyone wishes me to." If you are a rights holder and want them removed,
please open an issue.

## Commodore 64 emulator (VICE) and C64 ROMs

The C64 emulator is the [VICE](https://vice-emu.sourceforge.io/) libretro core built for the web by
[EmulatorJS](https://github.com/EmulatorJS/vice-libretro) (npm package `@emulatorjs/core-vice_x64`), licensed
under the GNU General Public License v2.0 or later. It is served unmodified (`c64/vice_x64_libretro.js` and
`.wasm`); its source is available at the link above. The core is a RetroArch build, and only the core is used, not
the EmulatorJS frontend.

The core's WebAssembly image embeds the C64 KERNAL, BASIC and character ROMs. They are not part of this
repository's source and are not covered by the licences above. They are copyright Commodore and its successors
and are distributed here on the same basis as in VICE and the EmulatorJS core. If you are a rights holder and want
them removed, please open an issue.

