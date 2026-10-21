namespace Irie.Dialects.Mem;

public enum MemOp : ushort
{
    /// <summary>
    /// %p : ptr = mem.symbol @name
    ///   Materialise the address of a module-level global as a ptr.
    ///   The operand layout is: def[0] = ptr vreg, use[0] = Symbol(name).
    /// </summary>
    Symbol,

    /// <summary>
    /// %v : i8  = mem.load.i8  %addr
    /// %v : i16 = mem.load.i16 %addr
    /// %v : i32 = mem.load.i32 %addr
    ///   Load a value of the indicated width from the pointer in %addr.
    ///   def[0] = result vreg, use[0] = ptr vreg.
    /// </summary>
    LoadI8,
    LoadI16,
    LoadI32,

    /// <summary>
    /// mem.store.i8  %addr, %val
    /// mem.store.i16 %addr, %val
    /// mem.store.i32 %addr, %val
    ///   Store %val to the pointer in %addr.
    ///   use[0] = ptr vreg, use[1] = value vreg.
    /// </summary>
    StoreI8,
    StoreI16,
    StoreI32,

    /// <summary>
    /// Post-legalization byte forms emitted by the MOS6502 target when widening
    /// mem.load / mem.store across the address. The offset is an Immediate that
    /// the isel materialises into the indirect-Y addressing mode's Y register
    /// (or, in the future, folds into an absolute-plus-offset addressing mode).
    /// </summary>
    /// <remarks>
    /// %v : i8 = mem.load.byte_at %addr, &lt;offset&gt;
    ///   def[0] = result byte vreg, use[0] = ptr vreg, use[1] = Immediate offset.
    /// </remarks>
    LoadByteAt,
    /// <summary>
    /// mem.store.byte_at %addr, &lt;offset&gt;, %val
    ///   use[0] = ptr vreg, use[1] = Immediate offset, use[2] = i8 value vreg.
    /// </summary>
    StoreByteAt,

    /// <summary>
    /// %p : ptr = mem.frame_addr &lt;slot_index&gt;
    ///   Surface the address of the function's frame slot at the given index.
    ///   def[0] = ptr vreg, use[0] = Immediate slot index.
    ///   FrameLoweringPass rewrites this to `mem.symbol @&lt;func&gt;_local&lt;index&gt;`.
    /// </summary>
    FrameAddr,

    /// <summary>
    /// mem.fill %addr, %byte, &lt;count&gt;
    ///   Fill &lt;count&gt; bytes at %addr with the byte value %byte. use[0] = ptr
    ///   vreg, use[1] = i8 byte-pattern vreg, use[2] = Immediate count.
    ///   Used by IL `initobj` (count = type size, byte = 0). The legalizer
    ///   unrolls small known counts (≤ ~16) into per-byte mem.store.byte_at.
    /// </summary>
    Fill,

    /// <summary>
    /// %v : i64 = mem.load.i64 %addr
    /// mem.store.i64 %addr, %val
    ///   As the narrower load/store forms. Declared last so the numeric codes of
    ///   the earlier ops (serialised in the binary format) stay put.
    /// </summary>
    LoadI64,
    StoreI64,

    /// <summary>
    /// %q : ptr = mem.ptradd %p, %off
    ///   Byte-offset address arithmetic. def[0] = ptr, use[0] = ptr, use[1] = i16
    ///   offset (two's complement, so p - n is ptradd p, (0 - n)). No scaling.
    /// </summary>
    PtrAdd,

    /// <summary>
    /// %v : ptr = mem.load.ptr %addr
    /// mem.store.ptr %addr, %val
    ///   Load / store a pointer-valued datum (vtable pointer, reference field, ...).
    /// </summary>
    LoadPtr,
    StorePtr,

    /// <summary>
    /// mem.copy %dst, %src, %count
    ///   Copy %count bytes from %src to %dst (the blocks must not overlap).
    ///   use[0] = ptr vreg (destination), use[1] = ptr vreg (source),
    ///   use[2] = i16 vreg (byte count; unlike mem.fill's, not a constant).
    ///   Used by IL `cpblk`. The MOS6502 legalizer lowers it to a call to
    ///   the memcpy libcall.
    /// </summary>
    Copy,

    /// <summary>
    /// mem.set %dst, %byte, %count
    ///   Set %count bytes at %dst to %byte. use[0] = ptr vreg, use[1] = i8 vreg
    ///   (the byte), use[2] = i16 vreg (byte count; unlike mem.fill's, not a
    ///   constant). Used by IL `initblk`. The MOS6502 legalizer lowers it to a
    ///   call to the memset libcall.
    /// </summary>
    Set,
}
