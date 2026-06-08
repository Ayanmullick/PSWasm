namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/token.cs
// Browser note: this token model keeps only the token kinds currently needed by the PSWasm parser profile.
public enum PowerShellWasmTokenKind
{
    EndOfInput,
    NewLine,
    Semicolon,
    Pipe,
    Equals,
    Comma,
    LParen,
    RParen,
    LBrace,
    RBrace,
    At,
    AtLBrace,
    AtLParen,
    Plus,
    Minus,
    Star,
    Slash,
    Parameter,
    Variable,
    Number,
    StringLiteral,
    ExpandableStringLiteral,
    Identifier
}

public readonly record struct PowerShellWasmToken(
    PowerShellWasmTokenKind Kind,
    string Text,
    int Offset,
    int Length,
    bool HasLeadingWhitespace);
