namespace Irie.Dialects.Arith;

public enum ArithOp : ushort
{
    // %r = arith.addi %a, %b
    AddI,

    // %r = arith.subi %a, %b
    SubI,

    // %cond = arith.cmpi <pred>, %a, %b   (predicate is an ArithCmpPredicate)
    CmpI,

    // %r, %cout = arith.addi_with_carry %a, %b, %cin
    // Post-legalization narrowed-add form (replaces today's GenericAddCarry).
    AddICarry,

    // %r, %bout = arith.subi_with_borrow %a, %b, %bin
    // Post-legalization narrowed-sub form. borrow_in / borrow_out use 6502
    // C-flag polarity: 1 = no borrow (carry set), 0 = borrow (carry clear).
    // So the chain head's borrow-in is materialized as `arith.constant 1 : i1`
    // (SEC), symmetric with addi_with_carry whose head is `arith.constant 0 : i1`
    // (CLC). Same 5-operand shape as addi_with_carry.
    SubIBorrow,

    // %r = arith.constant <value>
    // Typed integer constant. Used by the legalizer to materialize the
    // chain-head carry-in `arith.constant 0 : i1` so every addi_with_carry
    // has a uniform 3-use shape.
    Constant,

    // %r = arith.select %cond, %a, %b
    // SSA conditional value: %r is %a when %cond (i1) is true, else %b. Both value
    // operands share %r's type. A materialized (value) select is lowered to a CFG
    // diamond by the target's pre-isel select-lowering pass (mirrors llvm-mos
    // MOSLowerSelect); the i1 selects of a wide-compare lexicographic tree are
    // re-fused into a CMP+branch ladder by the instruction selector.
    Select,

    // %r = arith.not %x   — logical negation of an i1 (mirrors llvm-mos buildNot).
    // Emitted by the legalizer's consumer-independent predicate normalization
    // (ne/ult/sge/… → eq/uge/slt + NOT). It is never materialized: the instruction
    // selector strips it at the consuming cf.cond_br by swapping the branch's
    // true/false targets.
    Not,

    // %r = arith.and %a, %b   /   arith.or   /   arith.xor
    // Bitwise ops on integers. Pure per-bit, so a wide op narrows into
    // independent per-byte ops (no carry chain). Appended after Not to keep
    // existing opcode numbering (and binary-MIR goldens) stable.
    And,
    Or,
    Xor,

    // %r = arith.shl %a, %n   /   arith.lshr   /   arith.ashr
    // Whole-value shifts left, logical right and arithmetic right. The shift
    // amount %n is an i8 vreg; %r has %a's type. The amount must be a constant
    // smaller than the value's width. A wide shift is lowered by the target's
    // legalizer to byte moves plus a chain of one-bit shifts through the carry.
    Shl,
    LShr,
    AShr,

    // %r = arith.muli %a, %b
    // Integer multiply (low half of the product). Appended last to keep existing
    // opcode numbering stable. No target legalizes a general multiply yet: the
    // generic ArithSimplifyPass strength-reduces a multiply by a constant power
    // of two to `arith.shl` (and by 1 to a copy), and anything else is rejected
    // by the legalizer until a runtime-helper libcall lowering exists.
    MulI,

    // %r = arith.udivi %a, %b   /   arith.sdivi   /   arith.uremi   /   arith.sremi
    // Unsigned / signed integer division and remainder (sdivi truncates toward
    // zero; sremi takes the dividend's sign). Division by zero is undefined.
    // Appended after MulI to keep existing opcode numbering stable. As with
    // muli, the generic ArithSimplifyPass strength-reduces the unsigned forms by
    // a constant power of two (to lshr / and) and the target legalizes the rest
    // as a runtime-helper libcall.
    UDivI,
    SDivI,
    URemI,
    SRemI,
}
