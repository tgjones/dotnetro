namespace Irie.Target.MOS6502;

/// <summary>
/// Physical register ids of the MOS6502 target: a=0, x=1, y=2, s=3, p=4, then the
/// flags c=5 n=6 v=7 z=8 i=9 d=10 b=11 (first-class physregs: mos6502.cmp defines
/// $n/$v/$z, mos6502.bgt reads them; pseudo.copy is never used for flag registers),
/// then RC(n) = 12 + n for the imaginary zero-page registers ($zp0, $zp1, …) and
/// Home(n) = 300 + n for function-local homes ($h0, $h1, …).
/// </summary>
/// <remarks>
/// The ABI window RC0..RC19 is fixed: RC0/RC1 are the soft stack pointer, RC2.. carry
/// arguments and results after A and X (CC_MOS, see MOS6502CallLowering), RC16/RC17
/// hold the target of the indirect-call trampoline and RC18/RC19 are pointer scratch.
/// No register is callee-saved. Zero-page overflow is a NotSupportedException
/// (CompilerDriver reports it as a Diagnostic); frame slots, which have an absolute
/// fallback, are demoted to absolute memory first.
/// </remarks>
public static class MOS6502Registers
{
    public const int A = 0;
    public const int X = 1;
    public const int Y = 2;
    public const int S = 3;
    public const int P = 4;
    /// <summary>carry flag</summary>
    public const int C = 5;

    /// <summary>
    /// Status-flag physical registers used by the new MIR dialect
    /// (mos6502.cmp, mos6502.bgt, etc.).
    /// </summary>
    public const int N = 6;
    public const int V = 7;
    public const int Z = 8;
    public const int I = 9;
    public const int D = 10;
    public const int B = 11;

    /// <summary>Imaginary zero-page registers: zp0=12, zp1=13, zp2=14, ...</summary>
    public static int RC(int n) => 12 + n;

    /// <summary>
    /// Function-local zero-page "homes": the allocator's unbounded pool of
    /// zero-page bytes for values that live across calls or do not fit the
    /// caller-saved registers. A home has no address of its own until the
    /// program-layout pass (MOS6502ProgramLayoutPass) decides where the function's
    /// block of homes sits in zero page, stacked above every callee's block so a
    /// call never clobbers a live home; that pass then rewrites each home to the
    /// `RC(n)` of its final address. Home ids start above RC31 and are printed
    /// `$h0, $h1, …`.
    /// </summary>
    public const int HomeBase = 300;
    public const int HomeCount = 96;
    public static int Home(int n) => HomeBase + n;
    public static bool IsHome(int regNum) => regNum >= HomeBase;

    public static string NameOf(int regNum) => regNum switch
    {
        A => "a",
        X => "x",
        Y => "y",
        S => "s",
        P => "p",
        C => "c",
        N => "n",
        V => "v",
        Z => "z",
        I => "i",
        D => "d",
        B => "b",
        _ when regNum >= HomeBase => $"h{regNum - HomeBase}",
        _ when regNum >= 12 => $"zp{regNum - 12}",
        _ => throw new ArgumentOutOfRangeException(nameof(regNum), $"Unknown register: {regNum}"),
    };

    public static int? TryParse(string name) => name switch
    {
        "a" => A,
        "x" => X,
        "y" => Y,
        "s" => S,
        "p" => P,
        "c" => C,
        "n" => N,
        "v" => V,
        "z" => Z,
        "i" => I,
        "d" => D,
        "b" => B,
        _ when name.Length > 1 && name[0] == 'h' && int.TryParse(name[1..], out var h) => Home(h),
        _ when name.Length > 2 && name.StartsWith("zp") && int.TryParse(name[2..], out var n) => RC(n),
        _ => null,
    };
}
