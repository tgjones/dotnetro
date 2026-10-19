using Irie.Mir;

namespace Irie.Target.MOS6502;

/// <summary>
/// The `mos6502` MIR dialect. Owns the target opcode enum and its dialect-side
/// metadata (names, terminator/side-effect bits). Registered via the target's
/// constructor by DialectRegistry, like the other dialects.
/// </summary>
/// <remarks>
/// Per-opcode operand classes and tied/implicit operands live on the
/// target-side TargetInstructionInfo (still under construction in later steps);
/// GetInstructionInfo here returns DialectInstructionInfo.Empty for now.
/// </remarks>
public sealed class MOS6502Dialect : Dialect
{
    public static new DialectId Id { get; private set; }

    public override string Prefix => "mos6502";

    public static OpcodeRef OpRef(MOS6502Op op) => new(Id, (ushort)op);

    private static readonly Dictionary<MOS6502Op, string> _names = new()
    {
        // Addressing-mode-agnostic
        [MOS6502Op.And] = "and",
        [MOS6502Op.Ora] = "ora",
        [MOS6502Op.Eor] = "eor",
        [MOS6502Op.Inc] = "inc",
        [MOS6502Op.Dec] = "dec",

        // LDA
        [MOS6502Op.LdaImm]       = "lda.imm",
        [MOS6502Op.LdaImmSymLo]  = "lda.imm.symlo",
        [MOS6502Op.LdaImmSymHi]  = "lda.imm.symhi",
        [MOS6502Op.LdaZp]        = "lda.zp",
        [MOS6502Op.LdaZpX]  = "lda.zpx",
        [MOS6502Op.LdaAbs]  = "lda.abs",
        [MOS6502Op.LdaAbsX] = "lda.absx",
        [MOS6502Op.LdaAbsY] = "lda.absy",
        [MOS6502Op.LdaIndX] = "lda.indx",
        [MOS6502Op.LdaIndY] = "lda.indy",

        // STA
        [MOS6502Op.StaZp]   = "sta.zp",
        [MOS6502Op.StaZpX]  = "sta.zpx",
        [MOS6502Op.StaAbs]  = "sta.abs",
        [MOS6502Op.StaAbsX] = "sta.absx",
        [MOS6502Op.StaAbsY] = "sta.absy",
        [MOS6502Op.StaIndX] = "sta.indx",
        [MOS6502Op.StaIndY] = "sta.indy",

        // LDX
        [MOS6502Op.LdxImm]  = "ldx.imm",
        [MOS6502Op.LdxImmSymLo] = "ldx.imm.symlo",
        [MOS6502Op.LdxImmSymHi] = "ldx.imm.symhi",
        [MOS6502Op.LdxZp]   = "ldx.zp",
        [MOS6502Op.LdxZpY]  = "ldx.zpy",
        [MOS6502Op.LdxAbs]  = "ldx.abs",
        [MOS6502Op.LdxAbsY] = "ldx.absy",

        // STX
        [MOS6502Op.StxZp]   = "stx.zp",
        [MOS6502Op.StxZpY]  = "stx.zpy",
        [MOS6502Op.StxAbs]  = "stx.abs",

        // LDY
        [MOS6502Op.LdyImm]  = "ldy.imm",
        [MOS6502Op.LdyZp]   = "ldy.zp",
        [MOS6502Op.LdyZpX]  = "ldy.zpx",
        [MOS6502Op.LdyAbs]  = "ldy.abs",
        [MOS6502Op.LdyAbsX] = "ldy.absx",

        // STY
        [MOS6502Op.StyZp]   = "sty.zp",
        [MOS6502Op.StyZpX]  = "sty.zpx",
        [MOS6502Op.StyAbs]  = "sty.abs",

        // ADC
        [MOS6502Op.AdcImm]  = "adc.imm",
        [MOS6502Op.AdcZp]   = "adc.zp",
        [MOS6502Op.AdcZpX]  = "adc.zpx",
        [MOS6502Op.AdcAbs]  = "adc.abs",
        [MOS6502Op.AdcAbsX] = "adc.absx",
        [MOS6502Op.AdcAbsY] = "adc.absy",
        [MOS6502Op.AdcIndX] = "adc.indx",
        [MOS6502Op.AdcIndY] = "adc.indy",

        // SBC
        [MOS6502Op.SbcImm]  = "sbc.imm",
        [MOS6502Op.SbcZp]   = "sbc.zp",
        [MOS6502Op.SbcZpX]  = "sbc.zpx",
        [MOS6502Op.SbcAbs]  = "sbc.abs",
        [MOS6502Op.SbcAbsX] = "sbc.absx",
        [MOS6502Op.SbcAbsY] = "sbc.absy",
        [MOS6502Op.SbcIndX] = "sbc.indx",
        [MOS6502Op.SbcIndY] = "sbc.indy",

        // AND
        [MOS6502Op.AndImm]  = "and.imm",
        [MOS6502Op.AndZp]   = "and.zp",
        [MOS6502Op.AndZpX]  = "and.zpx",
        [MOS6502Op.AndAbs]  = "and.abs",
        [MOS6502Op.AndAbsX] = "and.absx",
        [MOS6502Op.AndAbsY] = "and.absy",
        [MOS6502Op.AndIndX] = "and.indx",
        [MOS6502Op.AndIndY] = "and.indy",

        // ORA
        [MOS6502Op.OraImm]  = "ora.imm",
        [MOS6502Op.OraZp]   = "ora.zp",
        [MOS6502Op.OraZpX]  = "ora.zpx",
        [MOS6502Op.OraAbs]  = "ora.abs",
        [MOS6502Op.OraAbsX] = "ora.absx",
        [MOS6502Op.OraAbsY] = "ora.absy",
        [MOS6502Op.OraIndX] = "ora.indx",
        [MOS6502Op.OraIndY] = "ora.indy",

        // EOR
        [MOS6502Op.EorImm]  = "eor.imm",
        [MOS6502Op.EorZp]   = "eor.zp",
        [MOS6502Op.EorZpX]  = "eor.zpx",
        [MOS6502Op.EorAbs]  = "eor.abs",
        [MOS6502Op.EorAbsX] = "eor.absx",
        [MOS6502Op.EorAbsY] = "eor.absy",
        [MOS6502Op.EorIndX] = "eor.indx",
        [MOS6502Op.EorIndY] = "eor.indy",

        // CMP
        [MOS6502Op.CmpImm]  = "cmp.imm",
        [MOS6502Op.CmpZp]   = "cmp.zp",
        [MOS6502Op.CmpZpX]  = "cmp.zpx",
        [MOS6502Op.CmpAbs]  = "cmp.abs",
        [MOS6502Op.CmpAbsX] = "cmp.absx",
        [MOS6502Op.CmpAbsY] = "cmp.absy",
        [MOS6502Op.CmpIndX] = "cmp.indx",
        [MOS6502Op.CmpIndY] = "cmp.indy",

        // CPX
        [MOS6502Op.CpxImm] = "cpx.imm",
        [MOS6502Op.CpxZp]  = "cpx.zp",
        [MOS6502Op.CpxAbs] = "cpx.abs",

        // CPY
        [MOS6502Op.CpyImm] = "cpy.imm",
        [MOS6502Op.CpyZp]  = "cpy.zp",
        [MOS6502Op.CpyAbs] = "cpy.abs",

        // INC / DEC (memory)
        [MOS6502Op.IncZp]   = "inc.zp",
        [MOS6502Op.IncZpX]  = "inc.zpx",
        [MOS6502Op.IncAbs]  = "inc.abs",
        [MOS6502Op.IncAbsX] = "inc.absx",
        [MOS6502Op.DecZp]   = "dec.zp",
        [MOS6502Op.DecZpX]  = "dec.zpx",
        [MOS6502Op.DecAbs]  = "dec.abs",
        [MOS6502Op.DecAbsX] = "dec.absx",

        // Register inc/dec
        [MOS6502Op.Inx] = "inx",
        [MOS6502Op.Dex] = "dex",
        [MOS6502Op.Iny] = "iny",
        [MOS6502Op.Dey] = "dey",

        // ASL
        [MOS6502Op.AslAcc]  = "asl.acc",
        [MOS6502Op.AslZp]   = "asl.zp",
        [MOS6502Op.AslZpX]  = "asl.zpx",
        [MOS6502Op.AslAbs]  = "asl.abs",
        [MOS6502Op.AslAbsX] = "asl.absx",

        // LSR
        [MOS6502Op.LsrAcc]  = "lsr.acc",
        [MOS6502Op.LsrZp]   = "lsr.zp",
        [MOS6502Op.LsrZpX]  = "lsr.zpx",
        [MOS6502Op.LsrAbs]  = "lsr.abs",
        [MOS6502Op.LsrAbsX] = "lsr.absx",

        // ROL
        [MOS6502Op.RolAcc]  = "rol.acc",
        [MOS6502Op.RolZp]   = "rol.zp",
        [MOS6502Op.RolZpX]  = "rol.zpx",
        [MOS6502Op.RolAbs]  = "rol.abs",
        [MOS6502Op.RolAbsX] = "rol.absx",

        // ROR
        [MOS6502Op.RorAcc]  = "ror.acc",
        [MOS6502Op.RorZp]   = "ror.zp",
        [MOS6502Op.RorZpX]  = "ror.zpx",
        [MOS6502Op.RorAbs]  = "ror.abs",
        [MOS6502Op.RorAbsX] = "ror.absx",

        // Branches
        [MOS6502Op.Beq] = "beq",
        [MOS6502Op.Bne] = "bne",
        [MOS6502Op.Bcc] = "bcc",
        [MOS6502Op.Bcs] = "bcs",
        [MOS6502Op.Bmi] = "bmi",
        [MOS6502Op.Bpl] = "bpl",
        [MOS6502Op.Bvc] = "bvc",
        [MOS6502Op.Bvs] = "bvs",
        [MOS6502Op.Bgt] = "bgt",

        // Abstract frame accesses (addressing-mode-agnostic; expanded post-RA).
        [MOS6502Op.FrameLoadByte]  = "frame.load.byte",
        [MOS6502Op.FrameStoreByte] = "frame.store.byte",

        // Jumps / calls / returns
        [MOS6502Op.JmpAbs] = "jmp.abs",
        [MOS6502Op.JmpInd] = "jmp.ind",
        [MOS6502Op.JsrAbs] = "jsr.abs",
        [MOS6502Op.Rts]    = "rts",
        [MOS6502Op.Rti]    = "rti",

        // Stack
        [MOS6502Op.Pha] = "pha",
        [MOS6502Op.Pla] = "pla",
        [MOS6502Op.Php] = "php",
        [MOS6502Op.Plp] = "plp",

        // Register transfers
        [MOS6502Op.Tax] = "tax",
        [MOS6502Op.Txa] = "txa",
        [MOS6502Op.Tay] = "tay",
        [MOS6502Op.Tya] = "tya",
        [MOS6502Op.Tsx] = "tsx",
        [MOS6502Op.Txs] = "txs",

        // Flag operations
        [MOS6502Op.Clc] = "clc",
        [MOS6502Op.Sec] = "sec",
        [MOS6502Op.Cli] = "cli",
        [MOS6502Op.Sei] = "sei",
        [MOS6502Op.Clv] = "clv",
        [MOS6502Op.Cld] = "cld",
        [MOS6502Op.Sed] = "sed",

        // Misc
        [MOS6502Op.Nop] = "nop",
        [MOS6502Op.Brk] = "brk",
        [MOS6502Op.Shle] = "shle",
        [MOS6502Op.Lshre] = "lshre",

        // Unpinned operations (see the comment on MOS6502Op.Adc). The bare
        // and/ora/eor/inc/dec names above are the other members of the family.
        [MOS6502Op.Adc]   = "adc",
        [MOS6502Op.Sbc]   = "sbc",
        [MOS6502Op.CmpBr] = "cmp_br",
        [MOS6502Op.Cmp]   = "cmp",
        [MOS6502Op.Load]  = "load",
        [MOS6502Op.Store] = "store",
        [MOS6502Op.Const] = "const",
    };

    private static readonly Dictionary<string, MOS6502Op> _byName =
        _names.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    private static readonly HashSet<MOS6502Op> _terminators =
    [
        // RTS / RTI: function returns. JMP: unconditional transfer, no
        // fallthrough. Conditional branches (Beq/Bne/etc.) are NOT terminators
        // in our model — they may branch to their target, but control falls
        // through to the next instruction on the not-taken path. JSR returns
        // to the caller after the called function returns, so it's not a
        // terminator either.
        MOS6502Op.Rts, MOS6502Op.Rti,
        MOS6502Op.JmpAbs, MOS6502Op.JmpInd,
    ];

    public override string GetOpName(ushort code)
        => _names.TryGetValue((MOS6502Op)code, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown mos6502 opcode {code}.");

    public override bool TryParseOp(string name, out ushort code)
    {
        if (_byName.TryGetValue(name, out var op))
        {
            code = (ushort)op;
            return true;
        }
        code = 0;
        return false;
    }

    public override DialectInstructionInfo GetInstructionInfo(ushort code) =>
        (MOS6502Op)code switch
        {
            MOS6502Op.AdcZp  => AdcInfo,
            MOS6502Op.AdcImm => AdcInfo,
            MOS6502Op.AdcAbs => AdcInfo,
            MOS6502Op.SbcZp  => SbcInfo,
            MOS6502Op.SbcImm => SbcInfo,
            MOS6502Op.SbcAbs => SbcInfo,
            MOS6502Op.AslAcc or MOS6502Op.LsrAcc => ShiftInfo,
            MOS6502Op.RolAcc or MOS6502Op.RorAcc => RotateInfo,
            MOS6502Op.CmpZp  => CmpInfo,
            MOS6502Op.CmpImm => CmpInfo,
            MOS6502Op.CpxZp or MOS6502Op.CpxImm => CpxInfo,
            MOS6502Op.CpyZp or MOS6502Op.CpyImm => CpyInfo,
            MOS6502Op.AndZp or MOS6502Op.AndImm or MOS6502Op.AndAbs
                or MOS6502Op.OraZp or MOS6502Op.OraImm or MOS6502Op.OraAbs
                or MOS6502Op.EorZp or MOS6502Op.EorImm or MOS6502Op.EorAbs => LogicInfo,
            MOS6502Op.FrameLoadByte  => FrameLoadByteInfo,
            MOS6502Op.FrameStoreByte => FrameStoreByteInfo,

            // Hardware-stack push/pull, emitted bare by PEI (no operands). PLA
            // pulls into $a and sets N/Z from the pulled byte; PHA pushes $a.
            // Modelling these def/use lists is what lets post-PEI liveness (e.g.
            // CopyPropagationPass) see that a PLA kills the prior $a, so a copy
            // into $a immediately before it is dead.
            MOS6502Op.Pla => PlaInfo,
            MOS6502Op.Pha => PhaInfo,

            // Loads and register transfers update N/Z from the byte they move
            // (the 6502 LDA/LDX/LDY/TAX/TAY/TXA/TYA/TSX all set N and Z; TXS
            // does NOT touch the flags, so it is deliberately absent). The
            // late-optimization pass (MOS6502LateOptimizationPass) relies on
            // this to drop a redundant `cmp $r, #0` whose operand was just
            // produced by one of these — the N/Z it would set are already live.
            MOS6502Op.LdaImm or MOS6502Op.LdaZp or MOS6502Op.LdaZpX
                or MOS6502Op.LdaAbs or MOS6502Op.LdaAbsX or MOS6502Op.LdaAbsY
                or MOS6502Op.LdaIndX or MOS6502Op.LdaIndY
                or MOS6502Op.LdxImm or MOS6502Op.LdxZp or MOS6502Op.LdxZpY
                or MOS6502Op.LdxAbs or MOS6502Op.LdxAbsY
                or MOS6502Op.LdyImm or MOS6502Op.LdyZp or MOS6502Op.LdyZpX
                or MOS6502Op.LdyAbs or MOS6502Op.LdyAbsX
                or MOS6502Op.LdaImmSymLo or MOS6502Op.LdaImmSymHi
                or MOS6502Op.LdxImmSymLo or MOS6502Op.LdxImmSymHi
                or MOS6502Op.Inx or MOS6502Op.Iny or MOS6502Op.Dex or MOS6502Op.Dey
                or MOS6502Op.IncZp or MOS6502Op.DecZp
                or MOS6502Op.Tax or MOS6502Op.Tay
                or MOS6502Op.Txa or MOS6502Op.Tya
                or MOS6502Op.Tsx => NZFlagSettingInfo,

            _ => DialectInstructionInfo.Empty,
        };

    /// <summary>
    /// Loads and transfers that set N/Z as a side effect of moving a byte.
    /// Modelled as implicit defs so the late-optimization pass and RA know the
    /// N/Z flags are written. No operand classes / tied operands are declared
    /// here: these ops are only seen post-RA (their explicit operands are
    /// already physical / immediate by the time this matters).
    /// </summary>
    private static readonly DialectInstructionInfo NZFlagSettingInfo = new(
        ImplicitDefs: [
            MOS6502Registers.N,
            MOS6502Registers.Z,
        ]);

    /// <summary>PLA: pulls the top hardware-stack byte into $a and sets N/Z from it.</summary>
    private static readonly DialectInstructionInfo PlaInfo = new(
        ImplicitDefs: [
            MOS6502Registers.A,
            MOS6502Registers.N,
            MOS6502Registers.Z,
        ]);

    /// <summary>PHA: pushes $a onto the hardware stack (a pure use of $a, no flag effect).</summary>
    private static readonly DialectInstructionInfo PhaInfo = new(
        ImplicitUses: [MOS6502Registers.A]);

    /// <summary>
    /// Abstract frame byte accesses. The scratch each expansion uses is declared
    /// here as implicit defs to document the clobber contract; the instruction
    /// selector also materialises these as explicit PhysicalReg(IsImplicit)
    /// operands (like mos6502.cmp's $n/$z/$c), which is what RA actually reads to
    /// reserve them. No tied operands.
    /// </summary>
    /// <remarks>
    /// The load defines its value in $a (Ac) — where lda.indy / lda.zp lands it —
    /// and clobbers $y plus the $rc18/$rc19 pointer pair (the indirect-Y path).
    ///
    /// The store's value is a use in $a (Ac), PRESERVED through the expansion: the
    /// absolute path builds the $rc18/$rc19 pointer pair via $x (ldx/stx, not
    /// lda/sta) precisely so the value byte already sitting in $a survives until
    /// the sta.indy. The store therefore clobbers $x, $y, $rc18, $rc19 — but NOT
    /// $a (it is a preserved use, not a clobber).
    ///
    /// A CallerStackArg-flavored frame.load.byte (stack-argument access, built
    /// directly by MOS6502CallLowering pre-legalization) is narrower than this:
    /// it only ever clobbers $y, since MOS6502FrameLowering.LowerCallerStackArgAccess
    /// reads the live RC0/RC1 soft stack pointer rather than rebuilding a pointer
    /// into it. That instance declares its own (smaller) explicit implicit-def
    /// list rather than this shared per-opcode default.
    ///
    ///   %v : ac = frame.load.byte  @slot, #off   (def[0]=value)
    ///             frame.store.byte  @slot, #off, %v : ac
    /// </remarks>
    private static readonly DialectInstructionInfo FrameLoadByteInfo = new(
        ImplicitDefs: [MOS6502Registers.Y, MOS6502Registers.RC(18), MOS6502Registers.RC(19)]);

    private static readonly DialectInstructionInfo FrameStoreByteInfo = new(
        ImplicitDefs: [MOS6502Registers.X, MOS6502Registers.Y, MOS6502Registers.RC(18), MOS6502Registers.RC(19)]);

    /// <summary>
    /// The `mos6502.adc` addressing-mode variants (`adc.zp`, `adc.imm`,
    /// `adc.abs`). 2 defs + 3 uses; use[0] is tied to def[0]. All addressing
    /// modes share this operand block — the only difference is the opcode tag and
    /// whether use[1] is a register, immediate, or Symbol. (The Imag8 class on
    /// use[1] only constrains the .zp form, where the RHS is a register; the
    /// .imm/.abs forms carry an Immediate/Symbol there, which the operand-class
    /// machinery skips.)
    /// </summary>
    private static readonly DialectInstructionInfo AdcInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: carry_out
            MOS6502RegisterClass.Ac,    // use[0]: L (tied to def[0])
            MOS6502RegisterClass.Imag8, // use[1]: R
            MOS6502RegisterClass.Cc,    // use[2]: carry_in
        ],
        TiedOperands: [-1, -1, 0, -1, -1]);

    /// <summary>
    /// The `mos6502.sbc` variants (`sbc.zp`, `sbc.imm`, `sbc.abs`). Same shape as
    /// Adc: 2 defs + 3 uses; use[0] tied to def[0]. The carry-in here is the 6502
    /// borrow-inverted carry flag (1 = no borrow).
    /// </summary>
    private static readonly DialectInstructionInfo SbcInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: borrow_out (carry-flag polarity)
            MOS6502RegisterClass.Ac,    // use[0]: L (tied to def[0])
            MOS6502RegisterClass.Imag8, // use[1]: R
            MOS6502RegisterClass.Cc,    // use[2]: borrow_in (carry-flag polarity)
        ],
        TiedOperands: [-1, -1, 0, -1, -1]);

    /// <summary>
    /// `mos6502.asl.acc` / `mos6502.lsr.acc`: shift $a one bit, the bit shifted out
    /// landing in the carry. def[0]=$a (result), def[1]=$c (carry-out),
    /// use[0]=$a (tied to def[0]).
    /// </summary>
    private static readonly DialectInstructionInfo ShiftInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: carry_out
            MOS6502RegisterClass.Ac,    // use[0]: a (tied to def[0])
        ],
        TiedOperands: [-1, -1, 0]);

    /// <summary>
    /// `mos6502.rol.acc` / `mos6502.ror.acc`: as ShiftInfo, but the old carry is
    /// shifted in at the vacated end. use[1]=$c (carry-in).
    /// </summary>
    private static readonly DialectInstructionInfo RotateInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Cc,    // def[1]: carry_out
            MOS6502RegisterClass.Ac,    // use[0]: a (tied to def[0])
            MOS6502RegisterClass.Cc,    // use[1]: carry_in
        ],
        TiedOperands: [-1, -1, 0, -1]);

    /// <summary>
    /// The `mos6502.cmp` variants (`cmp.zp`, `cmp.imm`).
    /// Explicit operands: use[0]=a (in $a), use[1]=b. Sets flags $n, $z, $c as
    /// implicit defs — they live as separate PhysicalReg(IsImplicit=true)
    /// entries in the operand array so RA understands the clobber. No tied
    /// operands (cmp doesn't write back to its register).
    /// </summary>
    /// <remarks>
    /// use[0] is `Ac`, though the 6502 can compare A, X or Y against memory
    /// (CMP / CPX / CPY) and llvm-mos declares the operand as a GPR
    /// (`CMPImag8 (ins GPR:$l, Imag8:$r)`). Widening it to any of A/X/Y was tried,
    /// with the post-RA addressing-mode selector refining to `cpx` / `cpy` (that
    /// selector has since been removed): it shrank the loop-heavy __umodsi3
    /// helper by 18%, but cost +8 instructions (734 → 742) on the llvm-mos
    /// reference scoreboard — the colourer spends $x / $y on short compares
    /// without weighing the moves that costs elsewhere (see early-return). Revisit
    /// when the allocator's cost model can use the freedom.
    /// </remarks>
    private static readonly DialectInstructionInfo CmpInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // use[0]: a
            MOS6502RegisterClass.Imag8, // use[1]: b
        ],
        ImplicitDefs: [
            MOS6502Registers.N,
            MOS6502Registers.Z,
            MOS6502Registers.C,
        ]);

    /// <summary>
    /// The X / Y forms `cpx.*` / `cpy.*` the addressing-mode selector refines a
    /// cmp to when its left operand is in $x / $y: same shape as CmpInfo with the
    /// register fixed.
    /// </summary>
    private static readonly DialectInstructionInfo CpxInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Xc,
            MOS6502RegisterClass.Imag8,
        ],
        ImplicitDefs: [
            MOS6502Registers.N,
            MOS6502Registers.Z,
            MOS6502Registers.C,
        ]);

    private static readonly DialectInstructionInfo CpyInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Yc,
            MOS6502RegisterClass.Imag8,
        ],
        ImplicitDefs: [
            MOS6502Registers.N,
            MOS6502Registers.Z,
            MOS6502Registers.C,
        ]);

    /// <summary>
    /// The accumulator logic ops `mos6502.{and,ora,eor}.{zp,imm,abs}`: def[0]=$a
    /// (result), use[0]=$a (tied to def[0]), use[1]=the RHS (a zero-page register,
    /// immediate or Symbol; the Imag8 class only constrains the .zp form). The
    /// signed-compare sign-flip also emits eor.imm with virtual Ac vregs, so the
    /// tied-operand metadata is needed for the two-address pass and RA to share a
    /// single $a across the def/use.
    /// </summary>
    private static readonly DialectInstructionInfo LogicInfo = new(
        OperandClasses: [
            MOS6502RegisterClass.Ac,    // def[0]: result
            MOS6502RegisterClass.Ac,    // use[0]: a (tied to def[0])
            MOS6502RegisterClass.Imag8, // use[1]: rhs
        ],
        TiedOperands: [-1, 0, -1]);

    /// <summary>
    /// Target ops conservatively touch physregs/memory; treat them as having
    /// side effects for DCE purposes until a finer-grained model is in place.
    /// </summary>
    public override bool IsSideEffectFree(ushort code) => false;

    /// <summary>
    /// Of the ops that name a symbol, only a jsr (and a tail-call jmp) calls it; the
    /// loads of a symbol's address bytes and the accesses of a global do not.
    /// </summary>
    public override bool IsCall(ushort code) => (MOS6502Op)code is MOS6502Op.JsrAbs or MOS6502Op.JmpAbs;

    public override bool IsTerminator(ushort code) => _terminators.Contains((MOS6502Op)code);

    /// <summary>RTS / RTI return from the (sub)routine — the function-exit terminators.</summary>
    public override bool IsReturn(ushort code)
        => (MOS6502Op)code is MOS6502Op.Rts or MOS6502Op.Rti;

    public override bool IsArtifact(ushort code) => false;

    /// <summary>
    /// cmp_br's conditions are Immediates at use positions 2, 4, … (each followed
    /// by its BlockTarget); render them as `eq`, `cs`, … rather than numbers.
    /// </summary>
    public override bool TryFormatImmediateUse(ushort code, int useIndex, long value, out string text)
    {
        if ((MOS6502Op)code == MOS6502Op.CmpBr && useIndex >= 2 && useIndex % 2 == 0
            && Enum.IsDefined((CmpBrCondition)value))
        {
            text = CmpBrConditionNames.ToText((CmpBrCondition)value);
            return true;
        }
        text = string.Empty;
        return false;
    }

    public override bool TryParseImmediateUse(ushort code, int useIndex, string text, out long value)
    {
        if ((MOS6502Op)code == MOS6502Op.CmpBr && useIndex >= 2 && useIndex % 2 == 0
            && CmpBrConditionNames.TryFromText(text, out var condition))
        {
            value = (long)condition;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// The structural immediates of the unpinned ops: cmp_br's conditions, the
    /// offset of load / store, cmp's right operand, and const's value or address-byte
    /// selector.
    /// </summary>
    public override bool IsLegalImmediateOperand(ushort code, int useIndex) =>
        (MOS6502Op)code switch
        {
            MOS6502Op.CmpBr => useIndex == 1 || (useIndex >= 2 && useIndex % 2 == 0),
            MOS6502Op.Cmp => useIndex == 1,
            MOS6502Op.Const => true,
            MOS6502Op.Load or MOS6502Op.Store => true,
            _ => false,
        };

    internal override void OnRegistered(DialectId id) => Id = id;
}
