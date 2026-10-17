namespace Irie.Target.MOS6502;

public static class MOS6502Registers
{
    public const int A = 0;
    public const int X = 1;
    public const int Y = 2;
    public const int S = 3;
    public const int P = 4;
    public const int C = 5; // carry flag

    // Status-flag physical registers used by the new MIR dialect
    // (mos6502.cmp, mos6502.bgt, etc.).
    public const int N = 6;
    public const int V = 7;
    public const int Z = 8;
    public const int I = 9;
    public const int D = 10;
    public const int B = 11;

    // Imaginary zero-page registers: zp0=12, zp1=13, zp2=14, ...
    public static int RC(int n) => 12 + n;

    // Function-local zero-page "homes": the allocator's unbounded pool of
    // zero-page bytes for values that live across calls or do not fit the
    // caller-saved registers. A home has no address of its own until the
    // program-layout pass (MOS6502ProgramLayoutPass) decides where the function's
    // block of homes sits in zero page, stacked above every callee's block so a
    // call never clobbers a live home; that pass then rewrites each home to the
    // `RC(n)` of its final address. Home ids start above RC31 and are printed
    // `$h0, $h1, …`.
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
