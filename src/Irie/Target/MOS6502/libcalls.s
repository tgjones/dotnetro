; MOS6502 libcalls -- the routines the legalizer emits calls to for operations the
; 6502 cannot do inline: multiply, divide, remainder, variable-count shifts and
; block copies and fills.
; Names follow compiler-rt / llvm RuntimeLibcalls (see MOS6502LegalizerInfo).
;
; Assembled by MOS6502AssemblyParser at start-up, and linked into a program only
; if the program references them (MachineCodeLinker). `__rcN` is imaginary
; register N, a zero-page byte (the parser maps it to the target's address).
;
; Every routine follows CC_MOS (MOS6502CallLowering):
;   arguments   bytes in A, X, __rc2, __rc3, ... (LSB first, arguments in order)
;   results     the same registers
;   clobbers    A, X, Y, flags, __rc2..__rc19
;   preserves   __rc0, __rc1 (soft stack pointer)
; A routine that needs more than __rc2..__rc19 declares the extra bytes it
; scratches, from __rc20 up, with `.zeropage N`. MOS6502ProgramLayoutPass keeps
; every caller's zero-page bytes clear of that range, so the routine overwrites
; them freely and saves nothing. The declaration belongs to the entry point; the
; routines it reaches (a `JSR`/`JMP` away) share its bytes.
;
; Division by zero is undefined (the quotient is garbage, but the routine
; terminates), as are shifts by the operand width or more.

; i16 __mulhi3(i16 a, i16 b): shift-and-add over the bits of b.
; b = __rc2/3 (consumed), multiplicand = __rc4/5, product = __rc6/7.
__mulhi3:
    STA __rc4
    STX __rc5
    LDA #$00
    STA __rc6
    STA __rc7
.loop:
    LDA __rc2
    ORA __rc3
    BEQ .done
    LSR __rc3
    ROR __rc2
    BCC .skip
    CLC
    LDA __rc6
    ADC __rc4
    STA __rc6
    LDA __rc7
    ADC __rc5
    STA __rc7
.skip:
    ASL __rc4
    ROL __rc5
    JMP .loop
.done:
    LDA __rc6
    LDX __rc7
    RTS

; i32 __mulsi3(i32 a, i32 b): as __mulhi3.
; b = __rc4..7 (consumed), multiplicand = __rc8..11, product = __rc12..15.
__mulsi3:
    STA __rc8
    STX __rc9
    LDA __rc2
    STA __rc10
    LDA __rc3
    STA __rc11
    LDA #$00
    STA __rc12
    STA __rc13
    STA __rc14
    STA __rc15
.loop:
    LDA __rc4
    ORA __rc5
    ORA __rc6
    ORA __rc7
    BEQ .done
    LSR __rc7
    ROR __rc6
    ROR __rc5
    ROR __rc4
    BCC .skip
    CLC
    LDA __rc12
    ADC __rc8
    STA __rc12
    LDA __rc13
    ADC __rc9
    STA __rc13
    LDA __rc14
    ADC __rc10
    STA __rc14
    LDA __rc15
    ADC __rc11
    STA __rc15
.skip:
    ASL __rc8
    ROL __rc9
    ROL __rc10
    ROL __rc11
    JMP .loop
.done:
    LDA __rc14
    STA __rc2
    LDA __rc15
    STA __rc3
    LDA __rc12
    LDX __rc13
    RTS

; i64 __muldi3(i64 a, i64 b): as __mulhi3, shifting the multiplier a right and
; the multiplicand b = __rc8..15 left.
; multiplier = __rc16/17 and __rc2..7, product = __rc18..25.
__muldi3:
.zeropage 6
    STA __rc16
    STX __rc17
    LDA #$00
    LDX #$07
.clear:
    STA __rc18,X
    DEX
    BPL .clear
.loop:
    LDA __rc16
    ORA __rc17
    ORA __rc2
    ORA __rc3
    ORA __rc4
    ORA __rc5
    ORA __rc6
    ORA __rc7
    BEQ .done
    LSR __rc7
    ROR __rc6
    ROR __rc5
    ROR __rc4
    ROR __rc3
    ROR __rc2
    ROR __rc17
    ROR __rc16
    BCC .skip
    CLC
    LDA __rc18
    ADC __rc8
    STA __rc18
    LDA __rc19
    ADC __rc9
    STA __rc19
    LDA __rc20
    ADC __rc10
    STA __rc20
    LDA __rc21
    ADC __rc11
    STA __rc21
    LDA __rc22
    ADC __rc12
    STA __rc22
    LDA __rc23
    ADC __rc13
    STA __rc23
    LDA __rc24
    ADC __rc14
    STA __rc24
    LDA __rc25
    ADC __rc15
    STA __rc25
.skip:
    ASL __rc8
    ROL __rc9
    ROL __rc10
    ROL __rc11
    ROL __rc12
    ROL __rc13
    ROL __rc14
    ROL __rc15
    JMP .loop
.done:
    LDX #$00
.copy:
    LDA __rc20,X
    STA __rc2,X
    INX
    CPX #$06
    BNE .copy
    LDA __rc18
    LDX __rc19
    RTS

; i16 __ashlhi3(i16 a, i8 n): n = __rc2; the value is shifted in __rc4/5.
__ashlhi3:
    LDY __rc2
    BEQ .ret
    STA __rc4
    STX __rc5
.loop:
    ASL __rc4
    ROL __rc5
    DEY
    BNE .loop
    LDA __rc4
    LDX __rc5
.ret:
    RTS

; i16 __lshrhi3(i16 a, i8 n): n = __rc2; the value is shifted in __rc4/5.
__lshrhi3:
    LDY __rc2
    BEQ .ret
    STA __rc4
    STX __rc5
.loop:
    LSR __rc5
    ROR __rc4
    DEY
    BNE .loop
    LDA __rc4
    LDX __rc5
.ret:
    RTS

; i16 __ashrhi3(i16 a, i8 n): n = __rc2; the value is shifted in __rc4/5.
__ashrhi3:
    LDY __rc2
    BEQ .ret
    STA __rc4
    STX __rc5
.loop:
    LDA __rc5
    CMP #$80
    ROR __rc5
    ROR __rc4
    DEY
    BNE .loop
    LDA __rc4
    LDX __rc5
.ret:
    RTS

; i32 __ashlsi3(i32 a, i8 n): n = __rc4; the low half is shifted in __rc6/7, the high half in place.
__ashlsi3:
    LDY __rc4
    BEQ .ret
    STA __rc6
    STX __rc7
.loop:
    ASL __rc6
    ROL __rc7
    ROL __rc2
    ROL __rc3
    DEY
    BNE .loop
    LDA __rc6
    LDX __rc7
.ret:
    RTS

; i32 __lshrsi3(i32 a, i8 n): n = __rc4; the low half is shifted in __rc6/7, the high half in place.
__lshrsi3:
    LDY __rc4
    BEQ .ret
    STA __rc6
    STX __rc7
.loop:
    LSR __rc3
    ROR __rc2
    ROR __rc7
    ROR __rc6
    DEY
    BNE .loop
    LDA __rc6
    LDX __rc7
.ret:
    RTS

; i32 __ashrsi3(i32 a, i8 n): n = __rc4; the low half is shifted in __rc6/7, the high half in place.
__ashrsi3:
    LDY __rc4
    BEQ .ret
    STA __rc6
    STX __rc7
.loop:
    LDA __rc3
    CMP #$80
    ROR __rc3
    ROR __rc2
    ROR __rc7
    ROR __rc6
    DEY
    BNE .loop
    LDA __rc6
    LDX __rc7
.ret:
    RTS

; i64 __ashldi3(i64 a, i8 n): n = __rc8; the low half is shifted in __rc9/10, the rest in place.
__ashldi3:
    LDY __rc8
    BEQ .ret
    STA __rc9
    STX __rc10
.loop:
    ASL __rc9
    ROL __rc10
    ROL __rc2
    ROL __rc3
    ROL __rc4
    ROL __rc5
    ROL __rc6
    ROL __rc7
    DEY
    BNE .loop
    LDA __rc9
    LDX __rc10
.ret:
    RTS

; i64 __lshrdi3(i64 a, i8 n): n = __rc8; the low half is shifted in __rc9/10, the rest in place.
__lshrdi3:
    LDY __rc8
    BEQ .ret
    STA __rc9
    STX __rc10
.loop:
    LSR __rc7
    ROR __rc6
    ROR __rc5
    ROR __rc4
    ROR __rc3
    ROR __rc2
    ROR __rc10
    ROR __rc9
    DEY
    BNE .loop
    LDA __rc9
    LDX __rc10
.ret:
    RTS

; i64 __ashrdi3(i64 a, i8 n): n = __rc8; the low half is shifted in __rc9/10, the rest in place.
__ashrdi3:
    LDY __rc8
    BEQ .ret
    STA __rc9
    STX __rc10
.loop:
    LDA __rc7
    CMP #$80
    ROR __rc7
    ROR __rc6
    ROR __rc5
    ROR __rc4
    ROR __rc3
    ROR __rc2
    ROR __rc10
    ROR __rc9
    DEY
    BNE .loop
    LDA __rc9
    LDX __rc10
.ret:
    RTS

; Negates the 32-bit dividend (__rc8..11) in place.
__negsi_n:
    SEC
    LDA #$00
    SBC __rc8
    STA __rc8
    LDA #$00
    SBC __rc9
    STA __rc9
    LDA #$00
    SBC __rc10
    STA __rc10
    LDA #$00
    SBC __rc11
    STA __rc11
    RTS

; Negates the 32-bit divisor (__rc4..7) in place.
__negsi_d:
    SEC
    LDA #$00
    SBC __rc4
    STA __rc4
    LDA #$00
    SBC __rc5
    STA __rc5
    LDA #$00
    SBC __rc6
    STA __rc6
    LDA #$00
    SBC __rc7
    STA __rc7
    RTS

; Negates the 32-bit remainder (__rc12..15) in place.
__negsi_r:
    SEC
    LDA #$00
    SBC __rc12
    STA __rc12
    LDA #$00
    SBC __rc13
    STA __rc13
    LDA #$00
    SBC __rc14
    STA __rc14
    LDA #$00
    SBC __rc15
    STA __rc15
    RTS

; Negates the 64-bit dividend (__rc16/17, __rc2..7) in place.
__negdi_n:
    SEC
    LDA #$00
    SBC __rc16
    STA __rc16
    LDA #$00
    SBC __rc17
    STA __rc17
    LDA #$00
    SBC __rc2
    STA __rc2
    LDA #$00
    SBC __rc3
    STA __rc3
    LDA #$00
    SBC __rc4
    STA __rc4
    LDA #$00
    SBC __rc5
    STA __rc5
    LDA #$00
    SBC __rc6
    STA __rc6
    LDA #$00
    SBC __rc7
    STA __rc7
    RTS

; Negates the 64-bit divisor (__rc8..15) in place.
__negdi_d:
    SEC
    LDA #$00
    SBC __rc8
    STA __rc8
    LDA #$00
    SBC __rc9
    STA __rc9
    LDA #$00
    SBC __rc10
    STA __rc10
    LDA #$00
    SBC __rc11
    STA __rc11
    LDA #$00
    SBC __rc12
    STA __rc12
    LDA #$00
    SBC __rc13
    STA __rc13
    LDA #$00
    SBC __rc14
    STA __rc14
    LDA #$00
    SBC __rc15
    STA __rc15
    RTS

; Negates the 64-bit remainder (__rc18..25) in place.
__negdi_r:
    SEC
    LDA #$00
    SBC __rc18
    STA __rc18
    LDA #$00
    SBC __rc19
    STA __rc19
    LDA #$00
    SBC __rc20
    STA __rc20
    LDA #$00
    SBC __rc21
    STA __rc21
    LDA #$00
    SBC __rc22
    STA __rc22
    LDA #$00
    SBC __rc23
    STA __rc23
    LDA #$00
    SBC __rc24
    STA __rc24
    LDA #$00
    SBC __rc25
    STA __rc25
    RTS

; Entry points of the 32-bit divide family. Each loads the operation into Y and
; shares __divmod32:  bit 0 = signed, bit 1 = remainder wanted.
__udivsi3:
    LDY #$00
    JMP __divmod32
__umodsi3:
    LDY #$02
    JMP __divmod32
__divsi3:
    LDY #$01
    JMP __divmod32
__modsi3:
    LDY #$03
    JMP __divmod32

; __divmod32: restoring long division. The dividend n (__rc8..11) is shifted left
; into the remainder R (__rc12..15), and each time R >= the divisor d (__rc4..7)
; d is subtracted and a 1 shifted into n's low bit, so n ends as the quotient.
; __rc16 = the operation, __rc17 = signs to restore: bit 0 negates the quotient,
; bit 1 the remainder (C: the quotient is negative when the signs differ, the
; remainder takes the dividend's sign).
__divmod32:
    STY __rc16
    STA __rc8
    STX __rc9
    LDA __rc2
    STA __rc10
    LDA __rc3
    STA __rc11
    LDA #$00
    STA __rc17
    STA __rc12
    STA __rc13
    STA __rc14
    STA __rc15
    LDA __rc16
    AND #$01
    BEQ .divide
    LDA __rc11
    BPL .dividend_ok
    LDA #$03
    STA __rc17
    JSR __negsi_n
.dividend_ok:
    LDA __rc7
    BPL .divide
    LDA __rc17
    EOR #$01
    STA __rc17
    JSR __negsi_d
.divide:
    LDY #$20
.loop:
    ASL __rc8
    ROL __rc9
    ROL __rc10
    ROL __rc11
    ROL __rc12
    ROL __rc13
    ROL __rc14
    ROL __rc15
    BCS .subtract
    SEC
    LDA __rc12
    SBC __rc4
    LDA __rc13
    SBC __rc5
    LDA __rc14
    SBC __rc6
    LDA __rc15
    SBC __rc7
    BCC .next
.subtract:
    SEC
    LDA __rc12
    SBC __rc4
    STA __rc12
    LDA __rc13
    SBC __rc5
    STA __rc13
    LDA __rc14
    SBC __rc6
    STA __rc14
    LDA __rc15
    SBC __rc7
    STA __rc15
    INC __rc8
.next:
    DEY
    BNE .loop
    LDA __rc16
    AND #$02
    BNE .remainder
    LDA __rc17
    AND #$01
    BEQ .quotient
    JSR __negsi_n
.quotient:
    LDA __rc10
    STA __rc2
    LDA __rc11
    STA __rc3
    LDA __rc8
    LDX __rc9
    RTS
.remainder:
    LDA __rc17
    AND #$02
    BEQ .remainder_done
    JSR __negsi_r
.remainder_done:
    LDA __rc14
    STA __rc2
    LDA __rc15
    STA __rc3
    LDA __rc12
    LDX __rc13
    RTS

; Entry points of the 64-bit divide family, as the 32-bit ones.
__udivdi3:
    LDY #$00
    JMP __divmod64
__umoddi3:
    LDY #$02
    JMP __divmod64
__divdi3:
    LDY #$01
    JMP __divmod64
__moddi3:
    LDY #$03
    JMP __divmod64

; __divmod64: as __divmod32 on 64 bits. The dividend/quotient n is __rc16/17 and
; __rc2..7, the divisor d __rc8..15, the remainder R __rc18..25. That uses all of
; __rc2..19, so it takes __rc20..27 as well: R's upper six bytes, __rc26 = the
; operation and __rc27 = the signs to restore.
__divmod64:
.zeropage 8
    STA __rc16
    STX __rc17
    STY __rc26
    LDA #$00
    STA __rc27
    LDX #$07
.clear:
    STA __rc18,X
    DEX
    BPL .clear
    LDA __rc26
    AND #$01
    BEQ .divide
    LDA __rc7
    BPL .dividend_ok
    LDA #$03
    STA __rc27
    JSR __negdi_n
.dividend_ok:
    LDA __rc15
    BPL .divide
    LDA __rc27
    EOR #$01
    STA __rc27
    JSR __negdi_d
.divide:
    LDY #$40
.loop:
    ASL __rc16
    ROL __rc17
    ROL __rc2
    ROL __rc3
    ROL __rc4
    ROL __rc5
    ROL __rc6
    ROL __rc7
    ROL __rc18
    ROL __rc19
    ROL __rc20
    ROL __rc21
    ROL __rc22
    ROL __rc23
    ROL __rc24
    ROL __rc25
    BCS .subtract
    SEC
    LDA __rc18
    SBC __rc8
    LDA __rc19
    SBC __rc9
    LDA __rc20
    SBC __rc10
    LDA __rc21
    SBC __rc11
    LDA __rc22
    SBC __rc12
    LDA __rc23
    SBC __rc13
    LDA __rc24
    SBC __rc14
    LDA __rc25
    SBC __rc15
    BCC .next
.subtract:
    SEC
    LDA __rc18
    SBC __rc8
    STA __rc18
    LDA __rc19
    SBC __rc9
    STA __rc19
    LDA __rc20
    SBC __rc10
    STA __rc20
    LDA __rc21
    SBC __rc11
    STA __rc21
    LDA __rc22
    SBC __rc12
    STA __rc22
    LDA __rc23
    SBC __rc13
    STA __rc23
    LDA __rc24
    SBC __rc14
    STA __rc24
    LDA __rc25
    SBC __rc15
    STA __rc25
    INC __rc16
.next:
    DEY
    BNE .loop
    LDA __rc26
    AND #$02
    BNE .remainder
    LDA __rc27
    AND #$01
    BEQ .quotient
    JSR __negdi_n
.quotient:
    LDA __rc16
    STA __rc18
    LDA __rc17
    STA __rc19
    JMP .exit
.remainder:
    LDA __rc27
    AND #$02
    BEQ .remainder_done
    JSR __negdi_r
.remainder_done:
    LDX #$00
.copy:
    LDA __rc20,X
    STA __rc2,X
    INX
    CPX #$06
    BNE .copy
.exit:
    LDA __rc18
    LDX __rc19
    RTS

; void memcpy(void *dst, const void *src, u16 count): copy count bytes, whole pages
; first, then the remainder. The blocks must not overlap.
; dst = A/X (moved to __rc6/7), src = __rc2/3, count = __rc4/5. About 16 cycles a byte.
memcpy:
    STA __rc6
    STX __rc7
    LDY #$00
    LDX __rc5
    BEQ .tail
.page:
    LDA (__rc2),Y
    STA (__rc6),Y
    INY
    BNE .page
    INC __rc3
    INC __rc7
    DEX
    BNE .page
.tail:
    LDX __rc4
    BEQ .done
.byte:
    LDA (__rc2),Y
    STA (__rc6),Y
    INY
    DEX
    BNE .byte
.done:
    RTS

; void memset(void *dst, u8 value, u16 count): set count bytes, whole pages first, then
; the remainder. dst = A/X (moved to __rc5/6), value = __rc2, count = __rc3/4.
memset:
    STA __rc5
    STX __rc6
    LDA __rc2
    LDY #$00
    LDX __rc4
    BEQ .tail
.page:
    STA (__rc5),Y
    INY
    BNE .page
    INC __rc6
    DEX
    BNE .page
.tail:
    LDX __rc3
    BEQ .done
.byte:
    STA (__rc5),Y
    INY
    DEX
    BNE .byte
.done:
    RTS
