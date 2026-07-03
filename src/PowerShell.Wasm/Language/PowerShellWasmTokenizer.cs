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
            while (position < script.Length && IsBareWordCharacter(script[position]))
            {
                position++;
            }

            tokens.Add(new(PowerShellWasmTokenKind.Parameter, script[nameStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        void ReadVariable()
        {
            var tokenStart = position;
            position++;
            var nameStart = position;
            if (position < script.Length && script[position] is '?' or '^' or '$')
            {
                position++;
                tokens.Add(new(PowerShellWasmTokenKind.Variable, script[nameStart..position], tokenStart, position - tokenStart, leadingWhitespace));
                leadingWhitespace = false;
                return;
            }

            while (position < script.Length && IsVariableCharacter(script[position]))
            {
                position++;
            }

            tokens.Add(new(PowerShellWasmTokenKind.Variable, script[nameStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        void ReadString(PowerShellWasmTokenKind kind, char quote)
        {
            var tokenStart = position;
            position++;
            var value = new StringBuilder();
            while (position < script.Length)
            {
                var current = script[position++];
                if (current == '`' && quote == '"' && position < script.Length)
                {
                    value.Append(ReadEscapedCharacter(script[position++]));
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

            tokens.Add(new(kind, value.ToString(), tokenStart, position - tokenStart, leadingWhitespace));
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
                    position += 2;
                    tokens.Add(new(kind, text, tokenStart, position - tokenStart, leadingWhitespace));
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

    private static bool IsVariableCharacter(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or ':';

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
