using Irie.Dialects.Arith;
using Irie.Dialects.Cast;
using Irie.Dialects.Mem;
using Irie.Dialects.Pseudo;
using Irie.Mir;

namespace Irie.Target.MOS6502;

/// <summary>
/// MOS6502 legalizer for the unified-MIR pipeline. Mirrors the old
/// Irie.Target.MOS6502.MOS6502LegalizerInfo while talking in OpcodeRef and the
/// new dialect opcodes (arith.addi, arith.addi_with_carry, arith.constant,
/// pseudo.merge, pseudo.unmerge, pseudo.copy).
/// </summary>
public sealed class MOS6502LegalizerInfo : Irie.Target.LegalizerInfo
{
    /// <summary>
    /// Used to lower runtime-helper libcalls built during legalization. AbiLowering
    /// has already run by then, so a libcall cannot be left as a `call.func` for it
    /// to lower; the legalizer lowers it directly, as llvm-mos's createLibcall does
    /// through CallLowering::lowerCall.
    /// </summary>
    private readonly MOS6502CallLowering _callLowering = new();

    public override LegalityAction GetAction(OpcodeRef opcode, IRType type)
    {
        // A ptr is not an integer: only the ops that move, load/store, offset or
        // convert one are accepted (no arith.* op takes a ptr).
        if (type is PointerType ptrType)
            return GetPointerAction(opcode, ptrType);

        if (type is not IntegerType intType)
            return LegalityAction.Unsupported;

        if (opcode.Dialect == ArithDialect.Id)
        {
            return ((ArithOp)opcode.Code) switch
            {
                ArithOp.AddI       when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.AddI       when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                ArithOp.SubI       when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.SubI       when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // Bitwise ops are per-bit, so a wide op narrows into independent
                // per-byte ops with no carry chain.
                ArithOp.And or ArithOp.Or or ArithOp.Xor
                                   when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.And or ArithOp.Or or ArithOp.Xor
                                   when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // A boolean has no register of its own (it lives in a flag until
                // consumed): and/or/xor of i1s is done on materialized 0/1 bytes
                // (LowerBooleanLogic), as llvm-mos does for G_AND of s1.
                ArithOp.And or ArithOp.Or or ArithOp.Xor
                                   when intType.SizeInBits == 1  => LegalityAction.Custom,
                // Whole-value shifts by a constant are lowered to byte moves plus a
                // chain of one-bit shifts through the carry flag (LowerShift). The
                // one-bit ops are the 6502's ASL/ROL and LSR/ROR.
                ArithOp.Shl or ArithOp.LShr or ArithOp.AShr
                                   when intType.SizeInBits % 8 == 0 => LegalityAction.Custom,
                ArithOp.AddICarry  => LegalityAction.Legal,
                ArithOp.SubIBorrow => LegalityAction.Legal,
                // arith.cmpi's legality is keyed off operand[2] (the `a` value),
                // not the i1 def — see ArithDialect.GetInstructionInfo's
                // TypeOperandIndex: 2. Every integer compare is Custom-legalized
                // (LegalizeCmpI): the predicate is normalized consumer-
                // independently to the canonical {eq, uge, slt} set (inverting via
                // an arith.not), the signed-against-zero shape is narrowed to a
                // single high-byte sign test, and a wide compare is lowered to a
                // lexicographic i8 cmpi + i1 select tree. An i8 compare is left
                // legal for the selector's i8 cmpi+cond_br fusion.
                ArithOp.CmpI       => LegalityAction.Custom,
                ArithOp.Constant   when intType.SizeInBits == 1  => LegalityAction.Legal,
                ArithOp.Constant   when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.Constant   when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // Materialized (value) selects: i8 left legal for the target's
                // select-lowering pass (which runs after the legalizer); wider
                // narrows element-wise into per-byte i8 selects sharing the i1
                // condition. The i1 selects of a wide-compare lexicographic tree
                // are left legal for the instruction selector to re-fuse.
                ArithOp.Select     when intType.SizeInBits == 1  => LegalityAction.Legal,
                ArithOp.Select     when intType.SizeInBits == 8  => LegalityAction.Legal,
                ArithOp.Select     when intType.SizeInBits > 8   => LegalityAction.NarrowScalar,
                // arith.not (i1) marks a normalized-away inverse predicate; left
                // legal for the selector to strip at the consuming cf.cond_br.
                ArithOp.Not        when intType.SizeInBits == 1  => LegalityAction.Legal,
                // Multiply, divide and remainder have no 6502 instruction: each is a
                // runtime-helper libcall (LowerLibcall), the analogue of llvm-mos's
                // G_MUL / G_*DIV / G_*REM `.libcall()` actions. i8 / i16 operands
                // are widened to the helper's width first.
                ArithOp.MulI or ArithOp.UDivI or ArithOp.SDivI
                    or ArithOp.URemI or ArithOp.SRemI
                                   when intType.SizeInBits is 8 or 16 or 32 or 64 => LegalityAction.Custom,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == MOS6502Dialect.Id)
        {
            return (MOS6502Op)opcode.Code switch
            {
                MOS6502Op.Shle or MOS6502Op.Lshre when intType.SizeInBits == 8 => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return ((PseudoOp)opcode.Code) switch
            {
                PseudoOp.Copy    => LegalityAction.Legal,
                PseudoOp.Merge   => LegalityAction.Legal,
                PseudoOp.Unmerge => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == CastDialect.Id)
        {
            return ((CastOp)opcode.Code) switch
            {
                // cast.trunc lowers to a pseudo.extract / pseudo.merge of the
                // source vreg's low bytes via the Custom action below.
                CastOp.Trunc => LegalityAction.Custom,
                // cast.zext / cast.sext: the legalization query type is the def
                // (the wide result). Lowered to the source bytes plus a constant
                // 0 (zext) or a sign-broadcast byte (sext) for each high byte.
                // An i8 result can only come from an i1 source (LowerExtend).
                CastOp.Zext or CastOp.Sext when intType.SizeInBits >= 8 => LegalityAction.Custom,
                CastOp.PtrToInt when intType.SizeInBits == 16 => LegalityAction.Custom,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == MemDialect.Id)
        {
            // mem.symbol produces a ptr (legal in GetPointerAction) that the isel
            // pattern-matches through to the per-byte lda.imm.symlo/symhi ops.
            //
            // mem.load.i8 / mem.store.i8: rewritten to mem.load.byte_at /
            // mem.store.byte_at with offset 0 by Custom legalization.
            //
            // mem.load.i16/i32, mem.store.i16/i32: narrowed to a chain of
            // mem.load.byte_at / mem.store.byte_at at consecutive offsets,
            // wrapped in pseudo.unmerge / pseudo.merge to splice into / out
            // of the wider vreg. Same Custom path.
            //
            // mem.load.byte_at / mem.store.byte_at: the post-legalization
            // form the isel knows how to lower to lda.indy / sta.indy.
            return ((MemOp)opcode.Code) switch
            {
                MemOp.LoadI8      when intType.SizeInBits == 8  => LegalityAction.Custom,
                MemOp.LoadI16     when intType.SizeInBits == 16 => LegalityAction.Custom,
                MemOp.LoadI32     when intType.SizeInBits == 32 => LegalityAction.Custom,
                MemOp.StoreI8     when intType.SizeInBits == 8  => LegalityAction.Custom,
                MemOp.StoreI16    when intType.SizeInBits == 16 => LegalityAction.Custom,
                MemOp.StoreI32    when intType.SizeInBits == 32 => LegalityAction.Custom,
                MemOp.LoadI64     when intType.SizeInBits == 64 => LegalityAction.Custom,
                MemOp.StoreI64    when intType.SizeInBits == 64 => LegalityAction.Custom,
                MemOp.LoadByteAt  when intType.SizeInBits == 8  => LegalityAction.Legal,
                MemOp.StoreByteAt when intType.SizeInBits == 8  => LegalityAction.Legal,
                // mem.fill's TypeOperandIndex points at the byte-pattern (i8) operand;
                // the count is an Immediate that the Custom path unrolls into per-byte
                // mem.store.byte_at.
                MemOp.Fill        when intType.SizeInBits == 8  => LegalityAction.Custom,
                _ => LegalityAction.Unsupported,
            };
        }

        return LegalityAction.Unsupported;
    }

    private static LegalityAction GetPointerAction(OpcodeRef opcode, PointerType type)
    {
        if (type.SizeInBits != 16)
            return LegalityAction.Unsupported;

        if (opcode.Dialect == PseudoDialect.Id)
        {
            return ((PseudoOp)opcode.Code) switch
            {
                PseudoOp.Copy or PseudoOp.Merge or PseudoOp.Unmerge => LegalityAction.Legal,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == MemDialect.Id)
        {
            return ((MemOp)opcode.Code) switch
            {
                MemOp.Symbol   => LegalityAction.Legal,
                MemOp.LoadPtr or MemOp.StorePtr or MemOp.PtrAdd => LegalityAction.Custom,
                _ => LegalityAction.Unsupported,
            };
        }

        if (opcode.Dialect == CastDialect.Id && (CastOp)opcode.Code == CastOp.IntToPtr)
            return LegalityAction.Custom;

        return LegalityAction.Unsupported;
    }

    public override IRType GetNarrowType(OpcodeRef opcode, IRType fromType)
    {
        if (opcode.Dialect == ArithDialect.Id
            && ((ArithOp)opcode.Code == ArithOp.AddI
                || (ArithOp)opcode.Code == ArithOp.SubI
                || (ArithOp)opcode.Code == ArithOp.And
                || (ArithOp)opcode.Code == ArithOp.Or
                || (ArithOp)opcode.Code == ArithOp.Xor
                || (ArithOp)opcode.Code == ArithOp.Constant
                || (ArithOp)opcode.Code == ArithOp.Select))
            return IRType.I8;

        throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no narrow type defined for opcode {opcode}");
    }

    /// <summary>
    /// Custom legalization for the mem dialect: rewrites typed mem.load / mem.store
    /// ops into per-byte mem.load.byte_at / mem.store.byte_at chains. The byte
    /// form carries an Immediate offset that the isel feeds into the indirect-Y
    /// addressing mode's Y register, so consecutive bytes of a wide load/store
    /// read or write from address+0, address+1, …
    /// </summary>
    public override void LegalizeCustom(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Opcode.Dialect == MemDialect.Id)
        {
            switch ((MemOp)instr.Opcode.Code)
            {
                case MemOp.LoadI8:
                case MemOp.LoadI16:
                case MemOp.LoadI32:
                case MemOp.LoadI64:
                case MemOp.LoadPtr:
                    LowerWideLoad(instr, builder);
                    return;

                case MemOp.StoreI8:
                case MemOp.StoreI16:
                case MemOp.StoreI32:
                case MemOp.StoreI64:
                case MemOp.StorePtr:
                    LowerWideStore(instr, builder);
                    return;

                case MemOp.PtrAdd:
                    LowerPtrAdd(instr, builder);
                    return;

                case MemOp.Fill:
                    LowerMemFill(instr, builder);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for mem opcode {(MemOp)instr.Opcode.Code}");
        }

        if (instr.Opcode.Dialect == ArithDialect.Id
            && (ArithOp)instr.Opcode.Code is ArithOp.MulI or ArithOp.UDivI or ArithOp.SDivI
                or ArithOp.URemI or ArithOp.SRemI)
        {
            LowerLibcall(instr, builder);
            return;
        }

        if (instr.Opcode.Dialect == CastDialect.Id)
        {
            switch ((CastOp)instr.Opcode.Code)
            {
                case CastOp.Trunc:
                    LowerTrunc(instr, builder);
                    return;
                case CastOp.PtrToInt:
                case CastOp.IntToPtr:
                    LowerPointerCast(instr, builder);
                    return;
                case CastOp.Zext:
                case CastOp.Sext:
                    LowerExtend(instr, builder, signed: (CastOp)instr.Opcode.Code == CastOp.Sext);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for cast opcode {(CastOp)instr.Opcode.Code}");
        }

        if (instr.Opcode.Dialect == ArithDialect.Id)
        {
            switch ((ArithOp)instr.Opcode.Code)
            {
                case ArithOp.CmpI:
                    LegalizeCmpI(instr, builder);
                    return;
                case ArithOp.And or ArithOp.Or or ArithOp.Xor:
                    LowerBooleanLogic(instr, builder);
                    return;
                case ArithOp.Shl or ArithOp.LShr or ArithOp.AShr:
                    LowerShift(instr, builder);
                    return;
            }
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: no Custom legalization for arith opcode {(ArithOp)instr.Opcode.Code}");
        }

        throw new NotSupportedException(
            $"MOS6502LegalizerInfo: no Custom legalization for {instr.Opcode}");
    }

    /// <summary>
    /// %r : iN = arith.{shl,lshr,ashr} %a, %n  (%n a constant k &lt; N)
    /// </summary>
    /// <remarks>
    /// Split k = 8q + r. The q whole-byte part is pure byte renaming: the live bytes
    /// move q places and the vacated bytes are filled with 0 (the sign byte for
    /// ashr). The r remaining bits are r passes of a one-bit shift chain through the
    /// carry flag over the live bytes only — ASL/ROL from the low byte up for shl,
    /// LSR/ROR from the high byte down for lshr. An arithmetic shift seeds its chain
    /// with the sign: `x &gt;=u 0x80` is the carry-in of the top byte's ROR.
    /// </remarks>
    private void LowerShift(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } defReg
            || instr.Operands[1] is not VirtualReg { IsDefinition: false } valueReg
            || instr.Operands[2] is not VirtualReg { IsDefinition: false } amountReg)
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: arith.shift must have shape `%def = arith.op %a, %n`.");

        var op = (ArithOp)instr.Opcode.Code;
        var width = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type.SizeInBits;

        if (function.GetDefinition(amountReg.Id) is not
            { Operands: [_, Immediate amountImm] } amountDef
            || amountDef.Opcode != ArithDialect.OpRef(ArithOp.Constant))
        {
            LowerShiftLibcall(instr, builder, op, defReg, valueReg, amountReg);
            return;
        }

        // C# masks the count to the operand width before the IL shift.
        var amount = (int)(amountImm.Value & (width - 1));
        var byteCount = width / 8;
        var wholeBytes = amount / 8;
        var bits = amount % 8;

        var bytes = byteCount == 1 ? [valueReg.Id] : builder.BuildUnmerge(IRType.I8, valueReg.Id, byteCount);
        var zero = builder.BuildConstant(IRType.I8, 0);

        // Fill byte for the vacated positions.
        var fill = zero;
        if (op == ArithOp.AShr && wholeBytes > 0)
        {
            var negative = builder.BuildCmpI(ArithCmpPredicate.Slt, bytes[^1], zero);
            var ones = builder.BuildConstant(IRType.I8, 0xFF);
            fill = builder.BuildSelect(IRType.I8, negative, ones, zero);
        }

        // The live bytes, in ascending byte order, and where they land in the result.
        var liveCount = byteCount - wholeBytes;
        var result = new int[byteCount];
        var firstLive = op == ArithOp.Shl ? wholeBytes : 0;
        for (var i = 0; i < byteCount; i++)
        {
            var isLive = i >= firstLive && i < firstLive + liveCount;
            result[i] = isLive
                ? bytes[op == ArithOp.Shl ? i - wholeBytes : i + wholeBytes]
                : fill;
        }

        for (var pass = 0; pass < bits; pass++)
        {
            if (op == ArithOp.Shl)
            {
                var carry = builder.BuildConstant(IRType.I1, 0);
                for (var i = firstLive; i < firstLive + liveCount; i++)
                    (result[i], carry) = BuildShiftBit(builder, MOS6502Op.Shle, result[i], carry);
            }
            else
            {
                var top = firstLive + liveCount - 1;
                int carry;
                if (op == ArithOp.AShr)
                {
                    // Sign into carry: x >=u 0x80 (selects to CMP #$80, as llvm-mos does).
                    var limit = builder.BuildConstant(IRType.I8, 0x80);
                    carry = builder.BuildCmpI(ArithCmpPredicate.Uge, result[top], limit);
                }
                else
                {
                    carry = builder.BuildConstant(IRType.I1, 0);
                }
                for (var i = top; i >= firstLive; i--)
                    (result[i], carry) = BuildShiftBit(builder, MOS6502Op.Lshre, result[i], carry);
            }
        }

        if (byteCount == 1)
            function.ReplaceAllUsesOfRegister(defReg.Id, result[0]);
        else
            builder.BuildMergeInto(defReg.Id, result);
        builder.Remove(instr);
    }

    /// <summary>
    /// %r : iN = arith.{shl,lshr,ashr} %a, %n  (%n not a constant)  →  a call to the
    /// runtime shift helper (compiler-rt / llvm RuntimeLibcalls names, as llvm-mos
    /// does for a variable shift: __ashlhi3, __lshrsi3, __ashrdi3, …) with the value
    /// widened to the helper's width and the count passed as an i8. A count at or
    /// beyond the operand width is undefined, as in LLVM IR; the helpers just shift
    /// that many times. The value is zero-extended for shl and lshr (the low bits of
    /// a left shift do not depend on the extension) and sign-extended for ashr, then
    /// the result truncated back.
    /// </summary>
    private void LowerShiftLibcall(MirInstruction instr, MirBuilder builder, ArithOp op,
        VirtualReg defReg, VirtualReg valueReg, VirtualReg amountReg)
    {
        var function = builder.Function;
        var resultType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var callType = resultType.SizeInBits <= 16 ? IRType.I16
            : resultType.SizeInBits <= 32 ? IRType.I32
            : IRType.I64;

        var callee = (op, callType.SizeInBits) switch
        {
            (ArithOp.Shl, 16)  => "__ashlhi3",
            (ArithOp.LShr, 16) => "__lshrhi3",
            (ArithOp.AShr, 16) => "__ashrhi3",
            (ArithOp.Shl, 32)  => "__ashlsi3",
            (ArithOp.LShr, 32) => "__lshrsi3",
            (ArithOp.AShr, 32) => "__ashrsi3",
            (ArithOp.Shl, 64)  => "__ashldi3",
            (ArithOp.LShr, 64) => "__lshrdi3",
            _                  => "__ashrdi3",
        };

        var value = valueReg.Id;
        var widened = resultType.SizeInBits < callType.SizeInBits;
        if (widened)
            value = BuildCast(builder, op == ArithOp.AShr ? CastOp.Sext : CastOp.Zext, callType, value);

        var amount = amountReg.Id;
        if (((TypedVReg)function.GetVRegAnnotation(amount)).Type.SizeInBits > 8)
            amount = BuildCast(builder, CastOp.Trunc, IRType.I8, amount);

        var result = widened ? function.CreateVirtualRegister(callType) : defReg.Id;
        _callLowering.LowerCall(callee, [callType, IRType.I8], [value, amount], [callType], [result], builder);

        if (widened)
            builder.BuildInstruction(CastDialect.OpRef(CastOp.Trunc),
                new VirtualReg(defReg.Id, IsDefinition: true),
                new VirtualReg(result, IsDefinition: false));

        builder.Remove(instr);
    }

    /// <summary>%r, %cout = mos6502.{shle,lshre} %a, %cin : one-bit shift link of a chain.</summary>
    private static (int result, int carryOut) BuildShiftBit(
        MirBuilder builder, MOS6502Op op, int a, int carryIn)
    {
        var result   = builder.Function.CreateVirtualRegister(IRType.I8);
        var carryOut = builder.Function.CreateVirtualRegister(IRType.I1);
        builder.BuildInstruction(
            MOS6502Dialect.OpRef(op),
            new VirtualReg(result,   IsDefinition: true),
            new VirtualReg(carryOut, IsDefinition: true),
            new VirtualReg(a,        IsDefinition: false),
            new VirtualReg(carryIn,  IsDefinition: false));
        return (result, carryOut);
    }

    /// <summary>
    /// arith.cmpi legalization (mirrors llvm-mos MOSLegalizerInfo::legalizeICmp):
    ///   1. Normalize the predicate to the canonical {eq, uge, slt} set the 6502
    ///      flags express directly — *consumer-independently*, exactly as llvm-mos
    ///      does. Negation (ne/ult/sge → eq/uge/slt) inverts the predicate and
    ///      wraps the i1 result in an `arith.not` (the buildNot analogue); operand
    ///      swap (ule/ugt/sle/sgt) reverses the predicate and the a/b operands.
    ///      The selector strips the `arith.not` at the consuming cf.cond_br by
    ///      swapping its true/false targets, so no boolean is ever materialized.
    ///   2. Narrow the signed compare against a statically-zero RHS (`x &lt;s 0`) to
    ///      a single high-byte sign test — `arith.cmpi slt, lhsHigh : i8, 0` —
    ///      since only the most-significant byte's sign bit matters.
    ///   3. Lower any remaining wide (i16/i32) compare to a lexicographic tree of
    ///      per-byte i8 arith.cmpi combined with i1 arith.select (BuildWideCmpTree).
    ///      The tree's i1 root feeds the cond_br; the instruction selector re-fuses
    ///      the whole tree into a CMP+branch ladder (it is never materialized).
    /// </summary>
    /// <remarks>
    /// Because normalization no longer depends on the consumer, a compare feeding a
    /// materialized arith.select (lowered to a cond_br only later, by the target's
    /// select-lowering pass) is narrowed here just the same.
    /// </remarks>
    private static void LegalizeCmpI(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 4
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not Immediate predImm
            || instr.Operands[2] is not VirtualReg || instr.Operands[3] is not VirtualReg)
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: arith.cmpi must have shape `%def : i1 = arith.cmpi <pred>, %a, %b`.");

        // Step 1: normalize the predicate to {eq, uge, slt}. Negation is recorded
        // and applied once, as an arith.not on the result (consumer-independent);
        // operand swap mutates the cmpi in place.
        var predicate = (ArithCmpPredicate)predImm.Value;
        var negate = false;
        while (predicate is not (ArithCmpPredicate.Eq or ArithCmpPredicate.Uge or ArithCmpPredicate.Slt))
        {
            if (predicate is ArithCmpPredicate.Ne or ArithCmpPredicate.Ult or ArithCmpPredicate.Sge)
            {
                predicate = ArithCmpPredicateOps.Inverse(predicate);
                negate = !negate;
            }
            else // ule, ugt, sle, sgt
            {
                predicate = ArithCmpPredicateOps.Swapped(predicate);
                (instr.Operands[2], instr.Operands[3]) = (instr.Operands[3], instr.Operands[2]);
            }
        }
        instr.Operands[1] = new Immediate((long)predicate);

        // Apply the negation by routing the cmpi's def through a fresh vreg and
        // emitting `%origDef = arith.not %inner` right after the cmpi. Steps 2/3
        // (which may RAUW the cmpi's def or rebuild it into a tree) then flow
        // through the not automatically, so consumers see not(canonicalResult).
        if (negate)
        {
            var inner = function.CreateVirtualRegister(IRType.I1);
            builder.SetInsertionPointAfter(instr);
            builder.BuildInstruction(
                ArithDialect.OpRef(ArithOp.Not),
                new VirtualReg(defReg.Id, IsDefinition: true),
                new VirtualReg(inner,     IsDefinition: false));
            instr.Operands[0] = new VirtualReg(inner, IsDefinition: true);
        }

        // Step 2: signed compare against a constant 0 → high-byte sign test.
        if (predicate == ArithCmpPredicate.Slt
            && instr.Operands[3] is VirtualReg bReg
            && IsConstantZero(function, bReg.Id))
        {
            var aReg = (VirtualReg)instr.Operands[2];
            var aType = ((TypedVReg)function.GetVRegAnnotation(aReg.Id)).Type;
            if (aType.SizeInBits > 8)
            {
                var byteCount = aType.SizeInBits / 8;
                var bytes = builder.BuildUnmerge(IRType.I8, aReg.Id, byteCount);
                instr.Operands[2] = new VirtualReg(bytes[byteCount - 1], IsDefinition: false);
            }

            // Re-key onto an immediate 0. Using an Immediate (rather than a fresh
            // i8 zero constant) keeps the RHS out of the vreg world entirely, so no
            // dead `arith.constant 0` survives to the selector.
            instr.Operands[3] = new Immediate(0);
            return;
        }

        // Step 3: a general wide (i16/i32) compare → a lexicographic tree of
        // per-byte i8 arith.cmpi combined with arith.select (mirrors llvm-mos
        // MOSLegalizerInfo::legalizeICmp's buildSelect recursion). The tree's i1
        // result feeds the cond_br; the instruction selector re-fuses the whole
        // tree back into a CMP+branch ladder (it is never materialized as a
        // boolean). MirSelectLoweringPass leaves these i1 selects alone.
        var lhs = (VirtualReg)instr.Operands[2];
        var lhsType = ((TypedVReg)function.GetVRegAnnotation(lhs.Id)).Type;
        if (lhsType.SizeInBits > 8)
        {
            var rhs = (VirtualReg)instr.Operands[3];
            BuildWideCmpTree(instr, builder, predicate, lhs.Id, rhs.Id, lhsType.SizeInBits / 8);
        }
        // An i8 compare is left as-is for the selector's i8 cmpi+cond_br fusion.
    }

    /// <summary>
    /// Lexicographic per-byte lowering of a wide compare. For canonical predicate
    /// P on N-byte operands (byte index 0 = least significant), the most-
    /// significant byte is compared with P and the lower bytes with the unsigned
    /// "rest" predicate (uge→uge, eq→eq, slt→ult — the signed bias of the top byte
    /// is applied later by the selector, exactly as the old multi-byte ladder did):
    ///   cmp(P, [0..hi]) = select(cmpi eq, a[hi], b[hi]
    ///                            ? cmp(restP, [0..hi-1])
    ///                            : cmpi P, a[hi], b[hi])
    /// </summary>
    /// <remarks>
    /// The selector's re-fusion recovers (predicate, per-byte operands) from this
    /// shape and emits the ladder; the tree instructions are then erased.
    /// </remarks>
    private static void BuildWideCmpTree(
        MirInstruction cmpi, MirBuilder builder, ArithCmpPredicate predicate,
        int aVreg, int bVreg, int byteCount)
    {
        var function = builder.Function;
        builder.SetInsertionPointBefore(cmpi);

        var aBytes = builder.BuildUnmerge(IRType.I8, aVreg, byteCount);
        var bBytes = builder.BuildUnmerge(IRType.I8, bVreg, byteCount);

        var restPredicate = predicate == ArithCmpPredicate.Slt ? ArithCmpPredicate.Ult : predicate;
        var root = BuildLexicographic(builder, aBytes, bBytes, byteCount - 1, predicate, restPredicate);

        var defId = ((VirtualReg)cmpi.Operands[0]).Id;
        function.ReplaceAllUsesOfRegister(defId, root);
        builder.Remove(cmpi);
    }

    /// <summary>
    /// Build the i1 comparison vreg for bytes [0..hi]. The byte at `hi` is compared
    /// with `thisPredicate`; the recursion for lower bytes uses `restPredicate`.
    /// </summary>
    private static int BuildLexicographic(
        MirBuilder builder, int[] aBytes, int[] bBytes, int hi,
        ArithCmpPredicate thisPredicate, ArithCmpPredicate restPredicate)
    {
        if (hi == 0)
            return builder.BuildCmpI(thisPredicate, aBytes[0], bBytes[0]);

        var cmpHi = builder.BuildCmpI(thisPredicate, aBytes[hi], bBytes[hi]);
        var eqHi  = builder.BuildCmpI(ArithCmpPredicate.Eq, aBytes[hi], bBytes[hi]);
        var rest  = BuildLexicographic(builder, aBytes, bBytes, hi - 1, restPredicate, restPredicate);
        return builder.BuildSelect(IRType.I1, eqHi, rest, cmpHi);
    }

    /// <summary>
    /// True when `vreg` is defined by `arith.constant 0` of any width. Pure
    /// inspection — never mutates the IR.
    /// </summary>
    private static bool IsConstantZero(MirFunction function, int vreg)
    {
        var def = function.GetDefinition(vreg);
        return def is not null
            && def.Opcode.Dialect == ArithDialect.Id
            && (ArithOp)def.Opcode.Code == ArithOp.Constant
            && def.Operands is [_, Immediate { Value: 0 }];
    }

    /// <summary>
    /// %v : iN = mem.load.iN %p  →  per-byte mem.load.byte_at chain + pseudo.merge.
    /// The merge re-uses the original wide vreg as its def so downstream uses are
    /// untouched.
    /// </summary>
    private static void LowerWideLoad(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not VirtualReg addrReg || addrReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.load.iN must have shape `%def : iN = mem.load.iN %addr`.");
        }

        var defType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var byteCount = defType.SizeInBits / 8;

        var byteVregs = new int[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            byteVregs[i] = function.CreateVirtualRegister(IRType.I8);
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.LoadByteAt),
                new VirtualReg(byteVregs[i], IsDefinition: true),
                new VirtualReg(addrReg.Id,   IsDefinition: false),
                new Immediate(i));
        }

        if (byteCount == 1)
        {
            // Single byte: just RAUW the def. The byte vreg replaces the
            // wide def vreg (both i8 here — the wide form was already i8).
            function.ReplaceAllUsesOfRegister(defReg.Id, byteVregs[0]);
        }
        else
        {
            // Reuse the original wide vreg as the merge's def so downstream
            // consumers keep their operands intact.
            builder.BuildMergeInto(defReg.Id, byteVregs);
        }

        builder.Remove(instr);
    }

    /// <summary>
    /// mem.fill %addr, %byte, &lt;count&gt;  →  unroll into &lt;count&gt; mem.store.byte_at
    /// ops at offsets 0..count-1, all writing the same byte vreg. Loop-form for
    /// large counts is future work; the active tests use small struct sizes.
    /// </summary>
    private static void LowerMemFill(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[1] is not VirtualReg byteReg || byteReg.IsDefinition
            || instr.Operands[2] is not Immediate countImm)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.fill must have shape `mem.fill %addr, %byte, <count>`.");
        }

        var count = countImm.Value;
        if (count < 0 || count > 256)
        {
            throw new NotSupportedException(
                $"MOS6502LegalizerInfo: mem.fill count {count} is out of range (loop-form not implemented; expected 0..256).");
        }

        for (var i = 0; i < count; i++)
        {
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.StoreByteAt),
                new VirtualReg(addrReg.Id, IsDefinition: false),
                new Immediate(i),
                new VirtualReg(byteReg.Id, IsDefinition: false));
        }
        builder.Remove(instr);
    }

    /// <summary>
    /// %y : iN = cast.zext|sext %x : iM  (M &lt; N)  →  the M/8 source bytes followed by
    /// N/8 - M/8 high bytes, merged into the original def. A zero-extend's high
    /// byte is a constant 0. A sign-extend's is the sign broadcast: 0xFF when the
    /// source's top byte is negative, else 0 — `select (slt top, 0), 0xFF, 0`, one
    /// byte shared by every high byte. (llvm-mos derives it from a shift/borrow
    /// sequence; that needs a shift op, which Irie's arith dialect lacks.)
    /// arith.muli / udivi / sdivi / uremi / sremi → a call to a runtime helper
    /// (names follow compiler-rt / llvm RuntimeLibcalls: __mulhi3, __mulsi3,
    /// __udivsi3, __divsi3, __umodsi3, __modsi3). Multiply has an i16 and an i32
    /// helper; divide and remainder only an i32 one. A narrower operand is widened
    /// to the helper's width and the result truncated back: zero-extension for
    /// multiply (the low bits of a product don't depend on the extension) and for
    /// the unsigned forms, sign-extension for the signed forms. The extension and
    /// truncation are ordinary casts, legalized afterwards by the worklist.
    /// </summary>
    private void LowerLibcall(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands is not [VirtualReg { IsDefinition: true } defReg,
                                   VirtualReg { IsDefinition: false } aReg,
                                   VirtualReg { IsDefinition: false } bReg])
            throw new InvalidOperationException(
                $"MOS6502LegalizerInfo: {instr.Opcode} must have shape `%def = op %a, %b`.");

        var op = (ArithOp)instr.Opcode.Code;
        var resultType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var signed = op is ArithOp.SDivI or ArithOp.SRemI;
        var callType = resultType.SizeInBits == 64 ? IRType.I64
            : op == ArithOp.MulI && resultType.SizeInBits <= 16 ? IRType.I16
            : IRType.I32;

        var callee = (op, callType.SizeInBits) switch
        {
            (ArithOp.MulI, 16)  => "__mulhi3",
            (ArithOp.MulI, 32)  => "__mulsi3",
            (ArithOp.UDivI, 32) => "__udivsi3",
            (ArithOp.SDivI, 32) => "__divsi3",
            (ArithOp.URemI, 32) => "__umodsi3",
            (ArithOp.SRemI, 32) => "__modsi3",
            (ArithOp.MulI, 64)  => "__muldi3",
            (ArithOp.UDivI, 64) => "__udivdi3",
            (ArithOp.SDivI, 64) => "__divdi3",
            (ArithOp.URemI, 64) => "__umoddi3",
            (ArithOp.SRemI, 64) => "__moddi3",
            _ => throw new NotSupportedException($"MOS6502LegalizerInfo: no libcall for {op} {callType}."),
        };

        var a = aReg.Id;
        var b = bReg.Id;
        var widened = resultType.SizeInBits < callType.SizeInBits;
        if (widened)
        {
            a = BuildCast(builder, signed ? CastOp.Sext : CastOp.Zext, callType, a);
            b = BuildCast(builder, signed ? CastOp.Sext : CastOp.Zext, callType, b);
        }

        var result = widened ? function.CreateVirtualRegister(callType) : defReg.Id;
        _callLowering.LowerCall(callee, [callType, callType], [a, b], [callType], [result], builder);

        if (widened)
            builder.BuildInstruction(CastDialect.OpRef(CastOp.Trunc),
                new VirtualReg(defReg.Id, IsDefinition: true),
                new VirtualReg(result, IsDefinition: false));

        builder.Remove(instr);
    }

    private static int BuildCast(MirBuilder builder, CastOp op, IRType to, int source)
    {
        var result = builder.Function.CreateVirtualRegister(to);
        builder.BuildInstruction(CastDialect.OpRef(op),
            new VirtualReg(result, IsDefinition: true),
            new VirtualReg(source, IsDefinition: false));
        return result;
    }

    private static void LowerExtend(MirInstruction instr, MirBuilder builder, bool signed)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } defReg
            || instr.Operands[1] is not VirtualReg { IsDefinition: false } srcReg)
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: cast.zext/sext must have shape `%def : iN = cast.zext %src : iM`.");

        var defType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var srcType = ((TypedVReg)function.GetVRegAnnotation(srcReg.Id)).Type;

        // A boolean widens through a select of its 0/1 (zext) or 0/-1 (sext) byte.
        if (srcType.SizeInBits == 1)
        {
            var low = BuildBooleanByte(builder, srcReg.Id, signed);
            if (defType.SizeInBits == 8)
            {
                function.ReplaceAllUsesOfRegister(defReg.Id, low);
            }
            else
            {
                var widened = new int[defType.SizeInBits / 8];
                Array.Fill(widened, signed ? low : builder.BuildConstant(IRType.I8, 0));
                widened[0] = low;
                builder.BuildMergeInto(defReg.Id, widened);
            }
            builder.Remove(instr);
            return;
        }

        if (defType.SizeInBits <= srcType.SizeInBits
            || srcType.SizeInBits % 8 != 0 || defType.SizeInBits % 8 != 0)
            throw new InvalidOperationException(
                $"MOS6502LegalizerInfo: cast.zext/sext requires a wider, byte-multiple result " +
                $"(got {defType.DisplayName} ← {srcType.DisplayName}).");

        var srcByteCount = srcType.SizeInBits / 8;
        var defByteCount = defType.SizeInBits / 8;

        var srcBytes = srcByteCount == 1
            ? [srcReg.Id]
            : builder.BuildUnmerge(IRType.I8, srcReg.Id, srcByteCount);

        var zero = builder.BuildConstant(IRType.I8, 0);
        var high = zero;
        if (signed)
        {
            var negative = builder.BuildCmpI(ArithCmpPredicate.Slt, srcBytes[^1], zero);
            var ones = builder.BuildConstant(IRType.I8, 0xFF);
            high = builder.BuildSelect(IRType.I8, negative, ones, zero);
        }

        var parts = new int[defByteCount];
        for (var i = 0; i < defByteCount; i++)
            parts[i] = i < srcByteCount ? srcBytes[i] : high;

        builder.BuildMergeInto(defReg.Id, parts);
        builder.Remove(instr);
    }

    /// <summary>The i8 value of boolean `cond`: 1 (or 0xFF when `allOnes`) if set, else 0.</summary>
    private static int BuildBooleanByte(MirBuilder builder, int cond, bool allOnes)
    {
        var set = builder.BuildConstant(IRType.I8, allOnes ? 0xFF : 1);
        var clear = builder.BuildConstant(IRType.I8, 0);
        return builder.BuildSelect(IRType.I8, cond, set, clear);
    }

    /// <summary>
    /// %r : i1 = arith.{and,or,xor} %a : i1, %b : i1  →  the op on the operands'
    /// 0/1 bytes, then `!= 0` to get a boolean back. A consumer that widens the
    /// result (the usual case) re-materializes it with a select.
    /// </summary>
    private static void LowerBooleanLogic(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands is not [VirtualReg { IsDefinition: true } defReg,
                                   VirtualReg { IsDefinition: false } aReg,
                                   VirtualReg { IsDefinition: false } bReg])
            throw new InvalidOperationException(
                $"MOS6502LegalizerInfo: {instr.Opcode} must have shape `%def = op %a, %b`.");

        var a = BuildBooleanByte(builder, aReg.Id, allOnes: false);
        var b = BuildBooleanByte(builder, bReg.Id, allOnes: false);
        var combined = function.CreateVirtualRegister(IRType.I8);
        builder.BuildInstruction(instr.Opcode,
            new VirtualReg(combined, IsDefinition: true),
            new VirtualReg(a, IsDefinition: false),
            new VirtualReg(b, IsDefinition: false));

        var zero = builder.BuildConstant(IRType.I8, 0);
        var result = builder.BuildCmpI(ArithCmpPredicate.Ne, combined, zero);
        function.ReplaceAllUsesOfRegister(defReg.Id, result);
        builder.Remove(instr);
    }

    /// <summary>
    /// %y : iM = cast.trunc %x : iN  (M &lt; N)  →  pseudo.unmerge + pseudo.merge
    /// of the low M/8 byte vregs. The special case M = 8 reduces to a single
    /// pseudo.extract (the artifact combiner from step 6 folds it through any
    /// upstream pseudo.merge, so a truncate of a multi-byte construct collapses
    /// to a direct reference to the desired byte).
    /// </summary>
    private static void LowerTrunc(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg defReg || !defReg.IsDefinition
            || instr.Operands[1] is not VirtualReg srcReg || srcReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: cast.trunc must have shape `%def : iM = cast.trunc %src : iN`.");
        }

        var defType = ((TypedVReg)function.GetVRegAnnotation(defReg.Id)).Type;
        var srcType = ((TypedVReg)function.GetVRegAnnotation(srcReg.Id)).Type;
        if (defType.SizeInBits >= srcType.SizeInBits)
        {
            throw new InvalidOperationException(
                $"MOS6502LegalizerInfo: cast.trunc requires the target type to be narrower " +
                $"than the source (got {defType.DisplayName} ← {srcType.DisplayName}).");
        }

        var defByteCount = defType.SizeInBits / 8;
        var srcByteCount = srcType.SizeInBits / 8;

        if (defByteCount == 1)
        {
            // Single-byte truncation: surface as a pseudo.extract at offset 0
            // so the artifact combiner can fold it against an upstream merge.
            var byteVreg = builder.BuildExtract(IRType.I8, srcReg.Id, bitOffset: 0);
            function.ReplaceAllUsesOfRegister(defReg.Id, byteVreg);
            builder.Remove(instr);
            return;
        }

        // Wider-than-byte truncation: unmerge the source into its byte vregs
        // and re-merge only the low defByteCount of them into the original
        // def vreg.
        var sourceBytes = builder.BuildUnmerge(IRType.I8, srcReg.Id, srcByteCount);
        var lowBytes = new int[defByteCount];
        Array.Copy(sourceBytes, lowBytes, defByteCount);
        builder.BuildMergeInto(defReg.Id, lowBytes);
        builder.Remove(instr);
    }

    /// <summary>
    /// mem.store.iN %p, %v  →  pseudo.unmerge %v + per-byte mem.store.byte_at chain.
    /// For a single-byte store the unmerge is skipped (the value vreg is fed
    /// straight into mem.store.byte_at at offset 0).
    /// </summary>
    private static void LowerWideStore(MirInstruction instr, MirBuilder builder)
    {
        var function = builder.Function;
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg addrReg || addrReg.IsDefinition
            || instr.Operands[1] is not VirtualReg valReg  || valReg.IsDefinition)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.store.iN must have shape `mem.store.iN %addr, %val`.");
        }

        var valType = ((TypedVReg)function.GetVRegAnnotation(valReg.Id)).Type;
        var byteCount = valType.SizeInBits / 8;

        int[] byteVregs;
        if (byteCount == 1)
        {
            byteVregs = [valReg.Id];
        }
        else
        {
            byteVregs = builder.BuildUnmerge(IRType.I8, valReg.Id, byteCount);
        }

        for (var i = 0; i < byteCount; i++)
        {
            builder.BuildInstruction(
                MemDialect.OpRef(MemOp.StoreByteAt),
                new VirtualReg(addrReg.Id,   IsDefinition: false),
                new Immediate(i),
                new VirtualReg(byteVregs[i], IsDefinition: false));
        }

        builder.Remove(instr);
    }

    /// <summary>
    /// %q : ptr = mem.ptradd %p, %off  →  unmerge both to bytes, a 2-link
    /// addi_with_carry chain, merge back into the original ptr def (which keeps
    /// its ptr type).
    /// </summary>
    private static void LowerPtrAdd(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Operands.Length != 3
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } def
            || instr.Operands[1] is not VirtualReg { IsDefinition: false } ptr
            || instr.Operands[2] is not VirtualReg { IsDefinition: false } off)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: mem.ptradd must have shape `%def : ptr = mem.ptradd %p, %off`.");
        }

        var ptrParts = builder.BuildUnmerge(IRType.I8, ptr.Id, 2);
        var offParts = builder.BuildUnmerge(IRType.I8, off.Id, 2);
        var carry = builder.BuildConstant(IRType.I1, 0);
        var result = new int[2];
        for (var i = 0; i < 2; i++)
            (result[i], carry) = builder.BuildAddCarry(IRType.I8, ptrParts[i], offParts[i], carry);

        builder.BuildMergeInto(def.Id, result);
        builder.Remove(instr);
    }

    /// <summary>
    /// cast.ptrtoint / cast.inttoptr (same width): unmerge to bytes and merge
    /// into the other type; the artifact combiner folds back-to-back casts.
    /// </summary>
    private static void LowerPointerCast(MirInstruction instr, MirBuilder builder)
    {
        if (instr.Operands.Length != 2
            || instr.Operands[0] is not VirtualReg { IsDefinition: true } def
            || instr.Operands[1] is not VirtualReg { IsDefinition: false } src)
        {
            throw new InvalidOperationException(
                "MOS6502LegalizerInfo: pointer cast must have shape `%def = cast.{ptrtoint,inttoptr} %src`.");
        }

        var parts = builder.BuildUnmerge(IRType.I8, src.Id, 2);
        builder.BuildMergeInto(def.Id, parts);
        builder.Remove(instr);
    }
}
