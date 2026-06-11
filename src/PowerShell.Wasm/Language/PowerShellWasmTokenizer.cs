using System.Text;

namespace PSWasm.Language;

// PowerShell source references:
// - src/System.Management.Automation/engine/parser/tokenizer.cs
// - src/System.Management.Automation/engine/parser/CharTraits.cs
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
                    Add(PowerShellWasmTokenKind.Plus, "+", 1);
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
                    else
                    {
                        ReadIdentifier();
                    }

                    break;
                case '-':
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
                    else
                    {
                        Add(PowerShellWasmTokenKind.At, "@", 1);
                    }

                    break;
                case '$':
                    ReadVariable();
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
                    value.Append(script[position++]);
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
                while (position < script.Length && IsBareWordCharacter(script[position]) && script[position] != '.')
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

    private static bool IsBareWordCharacter(char ch) =>
        !char.IsWhiteSpace(ch) && ch is not ';' and not '|' and not '=' and not ',' and not '(' and not ')' and not '{' and not '}'
            and not '[' and not ']' and not '\'' and not '"' and not '+' and not '*' and not '/' and not '@';
}
