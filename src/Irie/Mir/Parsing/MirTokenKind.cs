namespace Irie.Mir.Parsing;

internal enum MirTokenKind
{
    // Punctuation
    At,
    Arrow,
    Colon,
    Equals,
    Comma,
    Dot,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Plus,

    // Value-carrying
    Identifier,    // word not otherwise classified (type names, opcode parts, class names)
    /// <summary>decimal integer literal, possibly negative</summary>
    Integer,
    /// <summary>%N — virtual register reference</summary>
    ValueRef,
    /// <summary>$N (numeric) or $name (symbolic) — physical register reference</summary>
    PhysRegRef,
    /// <summary>bbN — block label (header definition or operand reference)</summary>
    BlockLabel,
    /// <summary>"..." — quoted string literal (used for global initializers)</summary>
    String,

    Eof,
}
