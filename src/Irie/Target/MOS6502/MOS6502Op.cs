namespace Irie.Target.MOS6502;

/// <summary>
/// Opcodes in the `mos6502` MIR dialect. Two flavours coexist:
/// - Addressing-mode-agnostic ops (`And`, `Ora`, `Eor`, `Inc`, `Dec`) that the
///   placements classify.
/// - Concrete: the mnemonic-plus-addressing-mode forms that have a direct 6502
///   byte mapping. Mirrors MOS6502Opcode 1:1.
/// </summary>
/// <remarks>
/// Plus a few synthetic ops (e.g. `Bgt`) that PseudoExpansionPass lowers later
/// to combinations of real branches.
/// </remarks>
public enum MOS6502Op : ushort
{
    // ===== Addressing-mode-agnostic =====
    And,
    Ora,
    Eor,
    Inc,
    Dec,

    // ===== Concrete =====

    // LDA
    LdaImm,
    /// <summary>
    /// LDA immediate with the low or high byte of a Symbol address. Both encode
    /// as the LDA_Immediate byte (0xA9); the operand is a Symbol that the
    /// assembler resolves to the low (`&lt;sym`) or high (`&gt;sym`) half of the
    /// symbol's final address. Used to materialize the bytes of a `mem.symbol`
    /// result into zero page for indirect-Y addressing.
    /// </summary>
    LdaImmSymLo,
    LdaImmSymHi,
    LdaZp,
    LdaZpX,
    LdaAbs,
    LdaAbsX,
    LdaAbsY,
    LdaIndX,
    LdaIndY,

    // STA
    StaZp,
    StaZpX,
    StaAbs,
    StaAbsX,
    StaAbsY,
    StaIndX,
    StaIndY,

    // LDX
    LdxImm,
    /// <summary>
    /// LDX immediate with the low or high byte of a Symbol address. Both encode
    /// as the LDX_Immediate byte (0xA2); the operand is a Symbol resolved to the
    /// low (`&lt;sym`) or high (`&gt;sym`) half of the symbol's address. The $x
    /// counterpart of LdaImmSymLo/Hi — used by frame lowering to build a slot's
    /// zero-page pointer pair via $x, so a store value already parked in $a is
    /// preserved across the pointer setup.
    /// </summary>
    LdxImmSymLo,
    LdxImmSymHi,
    LdxZp,
    LdxZpY,
    LdxAbs,
    LdxAbsY,

    // STX
    StxZp,
    StxZpY,
    StxAbs,

    // LDY
    LdyImm,
    LdyZp,
    LdyZpX,
    LdyAbs,
    LdyAbsX,

    // STY
    StyZp,
    StyZpX,
    StyAbs,

    // ADC
    AdcImm,
    AdcZp,
    AdcZpX,
    AdcAbs,
    AdcAbsX,
    AdcAbsY,
    AdcIndX,
    AdcIndY,

    // SBC
    SbcImm,
    SbcZp,
    SbcZpX,
    SbcAbs,
    SbcAbsX,
    SbcAbsY,
    SbcIndX,
    SbcIndY,

    // AND
    AndImm,
    AndZp,
    AndZpX,
    AndAbs,
    AndAbsX,
    AndAbsY,
    AndIndX,
    AndIndY,

    // ORA
    OraImm,
    OraZp,
    OraZpX,
    OraAbs,
    OraAbsX,
    OraAbsY,
    OraIndX,
    OraIndY,

    // EOR
    EorImm,
    EorZp,
    EorZpX,
    EorAbs,
    EorAbsX,
    EorAbsY,
    EorIndX,
    EorIndY,

    // CMP
    CmpImm,
    CmpZp,
    CmpZpX,
    CmpAbs,
    CmpAbsX,
    CmpAbsY,
    CmpIndX,
    CmpIndY,

    // CPX
    CpxImm,
    CpxZp,
    CpxAbs,

    // CPY
    CpyImm,
    CpyZp,
    CpyAbs,

    // INC / DEC (memory)
    IncZp,
    IncZpX,
    IncAbs,
    IncAbsX,
    DecZp,
    DecZpX,
    DecAbs,
    DecAbsX,

    // Register inc/dec
    Inx,
    Dex,
    Iny,
    Dey,

    // ASL
    AslAcc,
    AslZp,
    AslZpX,
    AslAbs,
    AslAbsX,

    // LSR
    LsrAcc,
    LsrZp,
    LsrZpX,
    LsrAbs,
    LsrAbsX,

    // ROL
    RolAcc,
    RolZp,
    RolZpX,
    RolAbs,
    RolAbsX,

    // ROR
    RorAcc,
    RorZp,
    RorZpX,
    RorAbs,
    RorAbsX,

    // Branches
    Beq,
    Bne,
    Bcc,
    Bcs,
    Bmi,
    Bpl,
    Bvc,
    Bvs,

    /// <summary>
    /// Synthetic branch — expanded later into real branches by
    /// PseudoExpansionPass (or a target-internal peephole).
    /// </summary>
    Bgt,

    /// <summary>
    /// ===== Abstract frame accesses (the llvm-mos LDStk/STStk analogues) =====
    /// </summary>
    /// <remarks>
    /// Addressing-mode-agnostic byte load/store against a FrameSlot, carrying the
    /// slot's symbol + a byte offset. Emitted at instruction selection for any
    /// byte access whose address resolves to a frame slot; expanded post-RA by
    /// FrameAccessLoweringPass → MOS6502FrameLowering.LowerFrameAccess into the
    /// concrete sequence chosen from the slot's StackId (today: always absolute
    /// indirect-Y; Stage 3 adds the direct-zp branch). The value byte lives in
    /// $a and the scratch the lowering uses ($y, $rc0, $rc1) is declared as an
    /// implicit clobber, so RA models both ends — no register scavenging later.
    /// </remarks>
    FrameLoadByte,
    FrameStoreByte,

    // Jumps / calls / returns
    JmpAbs,
    JmpInd,
    JsrAbs,
    Rts,
    Rti,

    // Stack
    Pha,
    Pla,
    Php,
    Plp,

    // Register transfers
    Tax,
    Txa,
    Tay,
    Tya,
    Tsx,
    Txs,

    // Flag operations
    Clc,
    Sec,
    Cli,
    Sei,
    Clv,
    Cld,
    Sed,

    // Misc
    Nop,
    Brk,

    /// <summary>
    /// Pre-selection ops, built by the legalizer on typed vregs and lowered by the
    /// instruction selector (the analogues of llvm-mos's G_SHLE / G_LSHRE).
    /// %r, %cout = mos6502.shle %a, %cin   — one-bit shift left through the carry:
    ///   %r = (%a &lt;&lt; 1) | %cin, %cout = bit 7 of %a.
    /// %r, %cout = mos6502.lshre %a, %cin  — one-bit logical shift right:
    ///   %r = (%a &gt;&gt; 1) | (%cin &lt;&lt; 7), %cout = bit 0 of %a.
    /// A chain head passes `arith.constant 0 : i1` as %cin (ASL / LSR); later links
    /// pass the previous link's %cout (ROL / ROR). Chaining them shifts multi-byte
    /// values.
    /// </summary>
    Shle,
    Lshre,

    /// <summary>
    /// ===== Unpinned operations (shape selection) =====
    /// </summary>
    /// <remarks>
    /// Byte-level operations on typed SSA vregs that name no register and no
    /// addressing mode. MOS6502ShapeSelector emits them from the legalized generic
    /// MIR and a placement (MOS6502BeamPlacementPass) chooses, per
    /// operation, which variant of MOS6502Variants implements it. The bare
    /// `And`/`Ora`/`Eor`/`Inc`/`Dec` above are the unpinned forms of those families
    /// (`mos6502.and` is `and.zp` / `and.imm` / `and.abs` before a variant is
    /// chosen); `Shle`/`Lshre` are already unpinned.
    ///
    /// %r, %cout = mos6502.adc %a, %b, %cin     %r = %a + %b + %cin, %cout the carry
    /// %r, %cout = mos6502.sbc %a, %b, %cin     %r = %a - %b - !%cin, %cout the no-borrow
    ///                                          (6502 carry polarity: 1 = no borrow)
    /// %r = mos6502.inc|dec %a                  %r = %a + 1 | %a - 1 (no carry in or out)
    /// %r = mos6502.and|ora|eor %a, %b
    /// %c = mos6502.cmp %a, %b                  %c = (%a &gt;=u %b), the carry a CMP leaves: the
    ///                                          sign seed of an arithmetic shift right
    /// mos6502.cmp_br %a, %b, &lt;cc&gt;, T [, &lt;cc&gt;, T2]
    ///        compare %a with %b once, then branch to each target whose condition
    ///        &lt;cc&gt; (a CmpBrCondition) holds on the result of that single compare,
    ///        in order; control falls through when none does. One byte of a wide
    ///        compare ladder, or the whole of an i8 compare + branch.
    /// %r = mos6502.load @sym+off               a byte of static memory (a global, or
    ///                                          a frame slot: the placement decides)
    /// %r = mos6502.load %ptrLo, %ptrHi, off   the byte at (pointer + off)
    /// mos6502.store %v, @sym+off
    /// mos6502.store %v, %ptrLo, %ptrHi, off
    /// %v = mos6502.const &lt;imm&gt;                 an i8 / i1 constant
    /// %v = mos6502.const @sym, &lt;0|1&gt;           the low (0) or high (1) byte of an address
    /// </remarks>
    Adc,
    Sbc,
    CmpBr,
    Cmp,
    Load,
    Store,
    Const,
}
