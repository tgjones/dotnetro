// RUN: printf '\251\310\040\322\377\251\111\040\322\377\251\015\040\322\377\040\317\377\040\322\377\114\042\010' | @emulator --target-system c64 --input Ab

// Hand-assembled program for the C64 test emulator's KERNAL stubs (no compiler involved):
//
//   $080D  LDA #$C8 / JSR $FFD2   CHROUT of PETSCII 'H' (upper-case letters are $C1-$DA)
//          LDA #$49 / JSR $FFD2   'i' (lower-case letters are $41-$5A)
//          LDA #$0D / JSR $FFD2   CR, shown as a newline
//          JSR $FFCF / JSR $FFD2  CHRIN a character ('A' as PETSCII $C1) and echo it
//   $0822  JMP $0822              halt
//
// CHECK: Hi
// CHECK: A
