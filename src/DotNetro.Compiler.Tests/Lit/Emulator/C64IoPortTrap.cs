// RUN: printf '\251\001\205\001\114\021\010' | @emulator --target-system c64; true

// Hand-assembled `LDA #1 / STA $01 / JMP *`. Writing the 6510's memory-map port
// would remap ROM/RAM/IO on a real C64, so the test emulator fails the run (exit code 4).
//
// CHECK: Program accessed the 6510 I/O port ($0001) at PC=$0811
