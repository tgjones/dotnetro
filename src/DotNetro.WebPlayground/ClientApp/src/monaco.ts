import './monacoFeatures';
import * as runtime from 'monaco-vs/editor/editor.api.js';
import type * as Monaco from 'monaco-editor';
import EditorWorker from './editor.worker?worker';

(self as unknown as { MonacoEnvironment: Monaco.Environment }).MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

export const ASM_LANGUAGE = 'asm6502';

// Monarch tokenizer for the MOS6502 assembly Irie emits ($FF / #$FF syntax, `;` comments).
runtime.languages.register({ id: ASM_LANGUAGE });
runtime.languages.setMonarchTokensProvider(ASM_LANGUAGE, {
  ignoreCase: true,
  mnemonics: [
    'adc', 'and', 'asl', 'bcc', 'bcs', 'beq', 'bit', 'bmi', 'bne', 'bpl', 'brk', 'bvc', 'bvs', 'clc', 'cld', 'cli', 'clv',
    'cmp', 'cpx', 'cpy', 'dec', 'dex', 'dey', 'eor', 'inc', 'inx', 'iny', 'jmp', 'jsr', 'lda', 'ldx', 'ldy', 'lsr', 'nop',
    'ora', 'pha', 'php', 'pla', 'plp', 'rol', 'ror', 'rti', 'rts', 'sbc', 'sec', 'sed', 'sei', 'sta', 'stx', 'sty', 'tax',
    'tay', 'tsx', 'txa', 'txs', 'tya',
  ],
  tokenizer: {
    root: [
      [/;.*$/, 'comment'],
      [/^[A-Za-z_.][\w.$]*:/, 'type.identifier'], // label definition
      [/#?\$[0-9a-f]+/, 'number.hex'],
      [/#?<?>?\d+/, 'number'],
      [/#[<>]?[A-Za-z_][\w.$]*/, 'number'],
      [/[A-Za-z_.][\w.$]*/, { cases: { '@mnemonics': 'keyword', '@default': 'identifier' } }],
      [/[(),+\-*]/, 'delimiter'],
    ],
  },
} as Monaco.languages.IMonarchLanguage);

export const MIR_LANGUAGE = 'irie-mir';

// Monarch tokenizer for the MIR text `MirModule.Write` prints (`;` comments, %vregs, $physregs, @symbols).
runtime.languages.register({ id: MIR_LANGUAGE });
runtime.languages.setMonarchTokensProvider(MIR_LANGUAGE, {
  keywords: ['func', 'global', 'frame_slot', 'bytes', 'stackid', 'offset', 'liveins'],
  types: ['i1', 'i8', 'i16', 'i32', 'i64', 'ptr'],
  tokenizer: {
    root: [
      [/;.*$/, 'comment'],
      [/"/, 'string', '@string'],
      [/%\d+/, 'variable'], // virtual register
      [/\$[A-Za-z_]\w*/, 'variable.predefined'], // physical register
      [/@[\w.$<>]+/, 'type.identifier'], // symbol
      [/\bbb\d+:?/, 'type.identifier'], // block label / branch target
      [/0x[0-9a-fA-F]+/, 'number.hex'],
      [/-?\d+/, 'number'],
      [/[A-Za-z_]\w*(?:\.\w+)+/, 'keyword'], // dialect.op[.suffix]
      [/[A-Za-z_]\w*/, { cases: { '@keywords': 'keyword', '@types': 'type', '@default': 'identifier' } }],
      [/->|[:=,\[\]{}()]/, 'delimiter'],
    ],
    string: [
      [/[^\\"]+/, 'string'],
      [/\\./, 'string.escape'],
      [/"/, 'string', '@pop'],
    ],
  },
} as Monaco.languages.IMonarchLanguage);

// The alias import above bypasses the package's `exports` map, so the types come from the
// package root and are applied here.
export const monaco = runtime as unknown as typeof Monaco;
export type { Monaco };
