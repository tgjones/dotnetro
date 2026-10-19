namespace Irie.Target.MOS6502;

/// <summary>
/// MOS6502 register class IDs. 0 means "no class assigned"; all real classes are &gt;= 1.
/// </summary>
/// <remarks>
/// - Ac, Xc, Yc, Cc, Vc, Nc, Zc, Ic, Dc, Bc: single architectural registers
/// - Imag8: imaginary 8-bit zero-page registers (RC0..RCn)
/// - Anyi8: any 8-bit register (A, X, Y, or Imag8). Used for values that can be
///   stored in any 8-bit location but aren't constrained to a specific one. $y was
///   added to this class because the
///   llvm-mos references use $a/$x/$y freely for data, whereas Irie previously
///   parked everything in the zero-page pool and never touched $y.
/// </remarks>
public static class MOS6502RegisterClass
{
    public const int None  = 0;
    /// <summary>accumulator (A)</summary>
    public const int Ac    = 1;
    /// <summary>X index register</summary>
    public const int Xc    = 2;
    /// <summary>Y index register</summary>
    public const int Yc    = 3;
    /// <summary>C flag (single bit)</summary>
    public const int Cc    = 4;
    /// <summary>V flag (single bit)</summary>
    public const int Vc    = 5;
    /// <summary>imaginary 8-bit zero-page registers (RC0..RCn)</summary>
    public const int Imag8 = 6;
    public const int Anyi8 = 7; // any 8-bit register (A, X, Y, or Imag8)
    /// <summary>
    /// Status-flag classes for the new MIR dialect (one class per flag is the
    /// first-cut layout; can be unified
    /// later if RA freedom is never needed).
    /// </summary>
    public const int Nc    = 8;  // N flag
    /// <summary>Z flag</summary>
    public const int Zc    = 9;
    /// <summary>I flag (interrupt disable)</summary>
    public const int Ic    = 10;
    /// <summary>D flag (decimal mode)</summary>
    public const int Dc    = 11;
    /// <summary>B flag (break)</summary>
    public const int Bc    = 12;

    public static string? GetName(int classId) => classId switch
    {
        Ac    => "ac",
        Xc    => "xc",
        Yc    => "yc",
        Cc    => "cc",
        Vc    => "vc",
        Imag8 => "zp",
        Anyi8 => "any8",
        Nc    => "nc",
        Zc    => "zc",
        Ic    => "ic",
        Dc    => "dc",
        Bc    => "bc",
        _ => null,
    };

    public static bool TryParse(string name, out int classId)
    {
        classId = name switch
        {
            "ac"    => Ac,
            "xc"    => Xc,
            "yc"    => Yc,
            "cc"    => Cc,
            "vc"    => Vc,
            "zp"    => Imag8,
            "any8"  => Anyi8,
            "nc"    => Nc,
            "zc"    => Zc,
            "ic"    => Ic,
            "dc"    => Dc,
            "bc"    => Bc,
            _ => None,
        };
        return classId != None;
    }

    /// <summary>
    /// Returns the class that a value held in the given physical register belongs to.
    /// Used by selectors to constrain a vreg's class when copying from / to a phys reg.
    /// </summary>
    public static int OfPhysicalRegister(int physReg) => physReg switch
    {
        MOS6502Registers.A => Ac,
        MOS6502Registers.X => Xc,
        MOS6502Registers.Y => Yc,
        MOS6502Registers.C => Cc,
        MOS6502Registers.N => Nc,
        MOS6502Registers.V => Vc,
        MOS6502Registers.Z => Zc,
        MOS6502Registers.I => Ic,
        MOS6502Registers.D => Dc,
        MOS6502Registers.B => Bc,
        _ when physReg >= MOS6502Registers.RC(0) => Imag8,
        _ => None,
    };
}
