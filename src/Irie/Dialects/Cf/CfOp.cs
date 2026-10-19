namespace Irie.Dialects.Cf;

public enum CfOp : ushort
{
    /// <summary>cf.br bb1</summary>
    Br,

    /// <summary>cf.cond_br %cond, bb1, bb2(%x)</summary>
    CondBr,
}
