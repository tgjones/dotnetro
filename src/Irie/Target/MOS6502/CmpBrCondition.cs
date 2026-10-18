namespace Irie.Target.MOS6502;

// The condition under which one exit of `mos6502.cmp_br` is taken, evaluated on
// the flags of the compare that op performs (a CMP of %a with %b).
public enum CmpBrCondition
{
    Eq,   // Z set:   %a == %b
    Ne,   // Z clear: %a != %b
    Cs,   // C set:   %a >=u %b
    Cc,   // C clear: %a <u %b
    Mi,   // N set:   bit 7 of (%a - %b) set (a sign test when %b is 0)
    Pl,   // N clear
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
