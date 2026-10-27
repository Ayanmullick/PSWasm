using System.Text;

namespace PSWasm.Language;

// PowerShell source references:
// - src/System.Management.Automation/engine/parser/tokenizer.cs
// - src/System.Management.Automation/engine/parser/CharTraits.cs
// - src/System.Management.Automation/engine/parser/token.cs: expandable and literal string tokens, including here-string forms.
// Ternary reference: tokenization of '?' / ':' into QuestionMark / Colon tokens.
// Browser note: this tokenizer intentionally avoids desktop host/runtime dependencies.
public static class PowerShellWasmTokenizer
{
    public static IReadOnlyList<PowerShellWasmToken> Tokenize(string script)
    {
        var tokens = new List<PowerShellWasmToken>();
        var leadingWhitespace = false;
        var position = 0;

        while (position < script.Length)
        {
            var ch = script[position];
            if (ch is ' ' or '\t')
            {
                leadingWhitespace = true;
                position++;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                var start = position;
                if (ch == '\r' && position + 1 < script.Length && script[position + 1] == '\n')
                {
                    position += 2;
                }
                else
                {
                    position++;
                }

                tokens.Add(new(PowerShellWasmTokenKind.NewLine, "\n", start, position - start, false));
                leadingWhitespace = true;
                continue;
            }

            if (ch == '#')
            {
                while (position < script.Length && script[position] is not '\r' and not '\n')
                {
                    position++;
                }

                continue;
            }

            if (ch == '<' && position + 1 < script.Length && script[position + 1] == '#')
            {
                position += 2;
                while (position + 1 < script.Length && !(script[position] == '#' && script[position + 1] == '>'))
                {
                    position++;
                }

                if (position + 1 >= script.Length)
                {
                    throw new InvalidOperationException("The terminator '#>' is missing from the multiline comment.");
                }

                position += 2;
                leadingWhitespace = true;
                continue;
            }

            if (ch == '`' && position + 1 < script.Length && script[position + 1] is '\r' or '\n')
            {
                position++;
                if (script[position] == '\r' && position + 1 < script.Length && script[position + 1] == '\n')
                {
                    position += 2;
                }
                else
                {
                    position++;
                }

                leadingWhitespace = true;
                continue;
            }

            switch (ch)
            {
                case ';':
                    Add(PowerShellWasmTokenKind.Semicolon, ";", 1);
                    break;
                case '|':
                    if (position + 1 < script.Length && script[position + 1] == '|')
                    {
                        Add(PowerShellWasmTokenKind.PipelineChainOr, "||", 2);
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.Pipe, "|", 1);
                    }

                    break;
                case '&':
                    if (position + 1 < script.Length && script[position + 1] == '&')
                    {
                        Add(PowerShellWasmTokenKind.PipelineChainAnd, "&&", 2);
                    }
                    else
                    {
                        ReadIdentifier();
                    }

                    break;
                case '=':
                    Add(PowerShellWasmTokenKind.Equals, "=", 1);
                    break;
                case ',':
                    Add(PowerShellWasmTokenKind.Comma, ",", 1);
                    break;
                case ':':
                    if (position + 1 < script.Length && script[position + 1] == ':')
                    {
                        Add(PowerShellWasmTokenKind.DoubleColon, "::", 2);
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.Colon, ":", 1);
                    }

                    break;
                case '(':
                    Add(PowerShellWasmTokenKind.LParen, "(", 1);
                    break;
                case ')':
                    Add(PowerShellWasmTokenKind.RParen, ")", 1);
                    break;
                case '{':
                    Add(PowerShellWasmTokenKind.LBrace, "{", 1);
                    break;
                case '}':
                    Add(PowerShellWasmTokenKind.RBrace, "}", 1);
                    break;
                case '[':
                    Add(PowerShellWasmTokenKind.LBracket, "[", 1);
                    break;
                case ']':
                    Add(PowerShellWasmTokenKind.RBracket, "]", 1);
                    break;
                case '+':
                    if (position + 1 < script.Length && script[position + 1] == '+')
                    {
                        Add(PowerShellWasmTokenKind.PlusPlus, "++", 2);
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.Plus, "+", 1);
                    }

                    break;
                case '*':
                    Add(PowerShellWasmTokenKind.Star, "*", 1);
                    break;
                case '/':
                    Add(PowerShellWasmTokenKind.Slash, "/", 1);
                    break;
                case '%':
                    Add(PowerShellWasmTokenKind.Remainder, "%", 1);
                    break;
                case '.':
                    if (position + 1 < script.Length && script[position + 1] == '.')
                    {
                        Add(PowerShellWasmTokenKind.DotDot, "..", 2);
                    }
                    else
                    {
                        ReadIdentifier();
                    }

                    break;
                case '?':
                    if (position + 1 < script.Length && script[position + 1] == '?')
                    {
                        Add(PowerShellWasmTokenKind.QuestionQuestion, "??", 2);
                    }
                    else if (position + 1 < script.Length && script[position + 1] == '.')
                    {
                        Add(PowerShellWasmTokenKind.QuestionDot, "?.", 2);
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.Question, "?", 1);
                    }

                    break;
                case '!':
                    Add(PowerShellWasmTokenKind.Not, "!", 1);
                    break;
                case '-':
                    if (position + 1 < script.Length && script[position + 1] == '-')
                    {
                        Add(PowerShellWasmTokenKind.MinusMinus, "--", 2);
                        break;
                    }

                    if (TryReadOperator())
                    {
                        break;
                    }

                    if (position + 1 < script.Length && IsIdentifierStart(script[position + 1]))
                    {
                        ReadParameter();
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.Minus, "-", 1);
                    }

                    break;
                case '@':
                    if (position + 1 < script.Length && script[position + 1] == '{')
                    {
                        Add(PowerShellWasmTokenKind.AtLBrace, "@{", 2);
                    }
                    else if (position + 1 < script.Length && script[position + 1] == '(')
                    {
                        Add(PowerShellWasmTokenKind.AtLParen, "@(", 2);
                    }
                    else if (position + 1 < script.Length && script[position + 1] is '\'' or '"')
                    {
                        var quote = script[position + 1];
                        ReadHereString(
                            quote == '\''
                                ? PowerShellWasmTokenKind.StringLiteral
                                : PowerShellWasmTokenKind.ExpandableStringLiteral,
                            quote);
                    }
                    else
                    {
                        Add(PowerShellWasmTokenKind.At, "@", 1);
                    }

                    break;
                case '$':
                    if (position + 1 < script.Length && script[position + 1] == '(')
                    {
                        Add(PowerShellWasmTokenKind.DollarLParen, "$(", 2);
                    }
                    else
                    {
                        ReadVariable();
                    }

                    break;
                case '\'':
                    ReadString(PowerShellWasmTokenKind.StringLiteral, '\'');
                    break;
                case '"':
                    ReadString(PowerShellWasmTokenKind.ExpandableStringLiteral, '"');
                    break;
                default:
                    if (char.IsDigit(ch))
                    {
                        ReadNumber();
                    }
                    else
                    {
                        ReadIdentifier();
                    }

                    break;
            }
        }

        tokens.Add(new(PowerShellWasmTokenKind.EndOfInput, string.Empty, script.Length, 0, leadingWhitespace));
        return tokens;

        void Add(PowerShellWasmTokenKind kind, string text, int length)
        {
            var start = position;
            tokens.Add(new(kind, text, start, length, leadingWhitespace));
            position += length;
            leadingWhitespace = false;
        }

        bool TryReadOperator()
        {
            var tokenStart = position;
            var end = position + 1;
            while (end < script.Length && IsBareWordCharacter(script[end]))
            {
                end++;
            }

            var text = script[tokenStart..end];
            if (!PowerShellWasmTokenTraits.TryGetOperator(text, out var kind))
            {
                return false;
            }

            tokens.Add(new(kind, text, tokenStart, end - tokenStart, leadingWhitespace));
            position = end;
            leadingWhitespace = false;
            return true;
        }

        void ReadParameter()
        {
            var tokenStart = position;
            position++;
            var nameStart = position;
            while (position < script.Length && IsBareWordCharacter(script[position]) && script[position] != ':')
            {
                position++;
            }

            tokens.Add(new(PowerShellWasmTokenKind.Parameter, script[nameStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        void ReadVariable()
        {
            var tokenStart = position;
            if (!TryReadVariableName(script, position, out var name, out position))
            {
                position++;
                tokens.Add(new(PowerShellWasmTokenKind.Identifier, "$", tokenStart, 1, leadingWhitespace));
            }
            else tokens.Add(new(PowerShellWasmTokenKind.Variable, name, tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        void ReadString(PowerShellWasmTokenKind kind, char quote)
        {
            var tokenStart = position;
            position++;
            var value = new StringBuilder();
            HashSet<int>? literalDollarOffsets = null;
            HashSet<int>? variableBoundaryOffsets = null;
            while (position < script.Length)
            {
                var current = script[position++];
                if (quote == '"' && current == '$' && position < script.Length && script[position] == '{' &&
                    TryReadVariableName(script, position - 1, out _, out var variableEnd, inExpandableString: true))
                {
                    // Preserve braced-name escapes until interpolation; decoding `} here loses its delimiter meaning.
                    value.Append(script.AsSpan(position - 1, variableEnd - position + 1));
                    position = variableEnd;
                    continue;
                }
                if (current == '`' && quote == '"' && position < script.Length)
                {
                    var escaped = script[position++];
                    (variableBoundaryOffsets ??= []).Add(value.Length);
                    if (escaped == '$') (literalDollarOffsets ??= []).Add(value.Length);
                    value.Append(ReadEscapedCharacter(escaped));
                    continue;
                }

                if (current == quote)
                {
                    if (quote == '\'' && position < script.Length && script[position] == '\'')
                    {
                        value.Append('\'');
                        position++;
                        continue;
                    }

                    break;
                }

                value.Append(current);
            }

            tokens.Add(new(kind, value.ToString(), tokenStart, position - tokenStart, leadingWhitespace)
                { LiteralDollarOffsets = literalDollarOffsets, VariableBoundaryOffsets = variableBoundaryOffsets });
            leadingWhitespace = false;
        }

        void ReadHereString(PowerShellWasmTokenKind kind, char quote)
        {
            var tokenStart = position;
            position += 2;
            if (!TryConsumeLineEnding())
            {
                throw new InvalidOperationException("No characters are allowed after a here-string header but before the end of the line.");
            }

            var contentStart = position;
            while (position < script.Length)
            {
                if (IsLineStart(position) &&
                    script[position] == quote &&
                    position + 1 < script.Length &&
                    script[position + 1] == '@')
                {
                    var text = TrimFinalLineEnding(script[contentStart..position]);
                    IReadOnlySet<int>? literalDollarOffsets = null;
                    IReadOnlySet<int>? variableBoundaryOffsets = null;
                    if (quote == '"') text = DecodeExpandableText(text, out literalDollarOffsets, out variableBoundaryOffsets);
                    position += 2;
                    tokens.Add(new(kind, text, tokenStart, position - tokenStart, leadingWhitespace)
                        { LiteralDollarOffsets = literalDollarOffsets, VariableBoundaryOffsets = variableBoundaryOffsets });
                    leadingWhitespace = false;
                    return;
                }

                position++;
            }

            throw new InvalidOperationException("The string is missing the terminator: " + quote + "@.");

            bool TryConsumeLineEnding()
            {
                if (position >= script.Length)
                {
                    return false;
                }

                if (script[position] == '\r')
                {
                    position++;
                    if (position < script.Length && script[position] == '\n')
                    {
                        position++;
                    }

                    return true;
                }

                if (script[position] == '\n')
                {
                    position++;
                    return true;
                }

                return false;
            }

            bool IsLineStart(int index) =>
                index == 0 || script[index - 1] is '\r' or '\n';

            static string TrimFinalLineEnding(string text)
            {
                if (text.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    return text[..^2];
                }

                return text.EndsWith('\n') || text.EndsWith('\r')
                    ? text[..^1]
                    : text;
            }
        }

        void ReadNumber()
        {
            var tokenStart = position;
            var sawDecimalPoint = false;
            while (position < script.Length)
            {
                if (char.IsDigit(script[position]))
                {
                    position++;
                    continue;
                }

                if (script[position] == '.' && !sawDecimalPoint &&
                    !(position + 1 < script.Length && script[position + 1] == '.'))
                {
                    sawDecimalPoint = true;
                    position++;
                    continue;
                }

                break;
            }

            if (position > tokenStart && script[position - 1] == '.')
            {
                position--;
            }

            tokens.Add(new(PowerShellWasmTokenKind.Number, script[tokenStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        void ReadIdentifier()
        {
            var tokenStart = position;
            if (position < script.Length && script[position] == '.')
            {
                position++;
                while (position < script.Length && IsDottedMemberCharacter(script[position]))
                {
                    position++;
                }

                tokens.Add(new(PowerShellWasmTokenKind.Identifier, script[tokenStart..position], tokenStart, position - tokenStart, leadingWhitespace));
                leadingWhitespace = false;
                return;
            }

            while (position < script.Length && IsBareWordCharacter(script[position]))
            {
                position++;
            }

            if (position == tokenStart)
            {
                position++;
            }

            tokens.Add(new(PowerShellWasmTokenKind.Identifier, script[tokenStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }
    }

    private static bool IsIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_';

    // Behavioral adaptation of ScanVariable: both source expressions and string interpolation use the same name rules.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2872-L3179
    internal static bool TryReadVariableName(string text, int start, out string name, out int nextIndex,
        bool inExpandableString = false, IReadOnlySet<int>? variableBoundaryOffsets = null)
    {
        name = string.Empty;
        nextIndex = start;
        var position = start + 1;
        if (position >= text.Length || text[start] != '$' || variableBoundaryOffsets?.Contains(position) == true) return false;
        if (text[position] == '{')
        {
            var builder = new StringBuilder();
            position++;
            var closed = false;
            while (position < text.Length)
            {
                var current = text[position++];
                if (current == '}') { closed = true; break; }
                if (current == '`' && position < text.Length) current = ReadEscapedCharacter(text[position++]);
                else if (current == '{') throw new InvalidOperationException("An opening brace in a variable name must be escaped with a backtick.");
                else if (current == '"' && inExpandableString && position < text.Length && text[position] == '"') position++;
                builder.Append(current);
            }
            if (!closed) throw new InvalidOperationException("Missing closing '}' in variable reference.");
            name = builder.ToString();
            if (name.Length == 0) throw new InvalidOperationException("A variable reference must contain a name.");
        }
        else
        {
            var first = text[position];
            if (!(char.IsLetterOrDigit(first) || first is '_' or '?' or '^' or '$')) return false;
            position++;
            // $$, $?, and $^ are one-character names; '?' elsewhere remains part of an ordinary name.
            if (first is not ('$' or '?' or '^'))
            {
                while (position < text.Length)
                {
                    // A decoded escaped suffix belongs to the string, not to the preceding variable name.
                    if (variableBoundaryOffsets?.Contains(position) == true) break;
                    var current = text[position];
                    if (current == ':' && position + 1 < text.Length && text[position + 1] == ':') break;
                    if (!(char.IsLetterOrDigit(current) || current is '_' or '?' or ':')) break;
                    position++;
                }
            }
            name = text[(start + 1)..position];
        }
        if (name.IndexOf(':') == name.Length - 1)
            throw new InvalidOperationException("A scope or drive qualifier must be followed by a variable name.");
        nextIndex = position;
        return true;
    }

    // Native expandable strings decode escapes separately from discovering nested expressions.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2449-L2506
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2795-L2839
    private static string DecodeExpandableText(string text, out IReadOnlySet<int>? literalDollarOffsets,
        out IReadOnlySet<int>? variableBoundaryOffsets)
    {
        var value = new StringBuilder();
        HashSet<int>? escapedDollars = null;
        HashSet<int>? escapedCharacters = null;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '$' && index + 1 < text.Length && text[index + 1] == '{' &&
                TryReadVariableName(text, index, out _, out var variableEnd, inExpandableString: true))
            {
                value.Append(text.AsSpan(index, variableEnd - index));
                index = variableEnd - 1;
            }
            else if (current == '`' && index + 1 < text.Length)
            {
                var escaped = text[++index];
                (escapedCharacters ??= []).Add(value.Length);
                if (escaped == '$') (escapedDollars ??= []).Add(value.Length);
                value.Append(ReadEscapedCharacter(escaped));
            }
            else value.Append(current);
        }
        literalDollarOffsets = escapedDollars;
        variableBoundaryOffsets = escapedCharacters;
        return value.ToString();
    }

    private static char ReadEscapedCharacter(char ch) =>
        ch switch
        {
            '0' => '\0',
            'a' => '\a',
            'b' => '\b',
            'e' => '\u001b',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            _ => ch
        };

    private static bool IsBareWordCharacter(char ch) =>
        !char.IsWhiteSpace(ch) && ch is not ';' and not '|' and not '=' and not ',' and not '(' and not ')' and not '{' and not '}'
            and not '[' and not ']' and not '\'' and not '"' and not '+' and not '*' and not '/' and not '@';

    private static bool IsDottedMemberCharacter(char ch) =>
        IsBareWordCharacter(ch) && ch is not '.' and not '-' and not '?' and not '%' and not '$';
}
