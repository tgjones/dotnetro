namespace Irie.Dialects.Core;

public enum CoreOp : ushort
{
    /// <summary>
    /// core.return %v  — source-level return; lowered to pseudo.return by
    /// AbiLoweringPass.
    /// </summary>
    Return,
}
