namespace Irie.Target.MOS6502;

/// <summary>
/// The condition under which one exit of `mos6502.cmp_br` is taken, evaluated on
/// the flags of the compare that op performs (a CMP of %a with %b).
/// </summary>
public enum CmpBrCondition
{
    /// <summary>Z set:   %a == %b</summary>
    Eq,
    /// <summary>Z clear: %a != %b</summary>
    Ne,
    /// <summary>C set:   %a &gt;=u %b</summary>
    Cs,
    /// <summary>C clear: %a &lt;u %b</summary>
    Cc,
    /// <summary>N set:   bit 7 of (%a - %b) set (a sign test when %b is 0)</summary>
    Mi,
    /// <summary>N clear</summary>
    Pl,
}

public static class CmpBrConditionNames
{
    public static string ToText(CmpBrCondition condition) => condition switch
    {
        CmpBrCondition.Eq => "eq",
        CmpBrCondition.Ne => "ne",
        CmpBrCondition.Cs => "cs",
        CmpBrCondition.Cc => "cc",
        CmpBrCondition.Mi => "mi",
        CmpBrCondition.Pl => "pl",
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    public static bool TryFromText(string text, out CmpBrCondition condition)
    {
        switch (text)
        {
            case "eq": condition = CmpBrCondition.Eq; return true;
            case "ne": condition = CmpBrCondition.Ne; return true;
            case "cs": condition = CmpBrCondition.Cs; return true;
            case "cc": condition = CmpBrCondition.Cc; return true;
            case "mi": condition = CmpBrCondition.Mi; return true;
            case "pl": condition = CmpBrCondition.Pl; return true;
            default:   condition = default;           return false;
        }
    }
}
