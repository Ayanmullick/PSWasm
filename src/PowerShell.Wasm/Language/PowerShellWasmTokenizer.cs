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
            if (char.IsWhiteSpace(ch) && ch is not '\r' and not '\n')
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

            if (ch == '#' && !ContinuesGenericArgument())
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
                    else if (position + 1 < script.Length && PowerShellWasmNumericLiteral.IsDecimalDigit(script[position + 1]))
                    {
                        ReadNumber();
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
                case '\u2013':
                case '\u2014':
                case '\u2015':
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
                        Add(PowerShellWasmTokenKind.Minus, ch.ToString(), 1);
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
                    else if (position + 1 < script.Length && IsQuote(script[position + 1]))
                    {
                        var quote = script[position + 1];
                        ReadHereString(
                            IsSingleQuote(quote)
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
                case '\u2018':
                case '\u2019':
                case '\u201a':
                case '\u201b':
                    ReadString(PowerShellWasmTokenKind.StringLiteral, ch);
                    break;
                case '"':
                case '\u201c':
                case '\u201d':
                case '\u201e':
                    ReadString(PowerShellWasmTokenKind.ExpandableStringLiteral, ch);
                    break;
                default:
                    if (PowerShellWasmNumericLiteral.IsDecimalDigit(ch))
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

        bool ContinuesGenericArgument()
        {
            if (tokens.Count == 0 || tokens[^1].Offset + tokens[^1].Length != position) return false;
            var index = tokens.Count - 1;
            while (index >= 0)
            {
                if (tokens[index].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Parameter or PowerShellWasmTokenKind.Number or
                    PowerShellWasmTokenKind.Variable) return true;
                if (tokens[index].Kind == PowerShellWasmTokenKind.RParen)
                {
                    var depth = 1;
                    while (--index >= 0)
                    {
                        if (tokens[index].Kind == PowerShellWasmTokenKind.RParen) depth++;
                        else if (tokens[index].Kind is PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.DollarLParen or
                            PowerShellWasmTokenKind.AtLParen && --depth == 0) break;
                    }
                    if (index < 0 || tokens[index].Kind != PowerShellWasmTokenKind.DollarLParen) return false;
                }
                else if (tokens[index].Kind is not PowerShellWasmTokenKind.StringLiteral and not PowerShellWasmTokenKind.ExpandableStringLiteral)
                    return false;
                if (index == 0 || tokens[index].HasLeadingWhitespace ||
                    tokens[index - 1].Offset + tokens[index - 1].Length != tokens[index].Offset) return false;
                index--;
            }
            return false;
        }

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

        // Adapt literal/expandable scanning, including doubled delimiters and an error before executing unterminated input.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2317-L2353
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2449-L2515
        void ReadString(PowerShellWasmTokenKind kind, char quote)
        {
            var tokenStart = position;
            position++;
            var value = new StringBuilder();
            var expandable = kind == PowerShellWasmTokenKind.ExpandableStringLiteral;
            var closed = false;
            HashSet<int>? literalDollarOffsets = null;
            HashSet<int>? variableBoundaryOffsets = null;
            while (position < script.Length)
            {
                var current = script[position++];
                if (expandable && current == '$' && position < script.Length && script[position] == '(')
                {
                    position--;
                    value.Append(ReadExpandableSubexpression(script, ref position, hereString: false));
                    continue;
                }
                if (expandable && current == '$' && position < script.Length && script[position] == '{' &&
                    TryReadVariableName(script, position - 1, out _, out var variableEnd, inExpandableString: true))
                {
                    // Preserve braced-name escapes until interpolation; decoding `} here loses its delimiter meaning.
                    value.Append(script.AsSpan(position - 1, variableEnd - position + 1));
                    position = variableEnd;
                    continue;
                }
                if (current == '`' && expandable && position < script.Length)
                {
                    var escaped = ReadEscape(script, ref position);
                    (variableBoundaryOffsets ??= []).Add(value.Length);
                    if (escaped == "$") (literalDollarOffsets ??= []).Add(value.Length);
                    value.Append(escaped);
                    continue;
                }

                if (expandable ? IsDoubleQuote(current) : IsSingleQuote(current))
                {
                    if (position < script.Length && (expandable ? IsDoubleQuote(script[position]) : IsSingleQuote(script[position])))
                    {
                        value.Append(script[position++]);
                        continue;
                    }

                    closed = true;
                    break;
                }

                value.Append(current);
            }

            if (!closed) throw new InvalidOperationException("The string is missing the terminator: " + quote + ".");

            tokens.Add(new(kind, value.ToString(), tokenStart, position - tokenStart, leadingWhitespace)
                { LiteralDollarOffsets = literalDollarOffsets, VariableBoundaryOffsets = variableBoundaryOffsets });
            leadingWhitespace = false;
        }

        void ReadHereString(PowerShellWasmTokenKind kind, char quote)
        {
            var tokenStart = position;
            position += 2;
            // Native permits horizontal whitespace between the here-string header and its required newline.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2578-L2593
            while (position < script.Length && char.IsWhiteSpace(script[position]) && script[position] is not '\r' and not '\n')
                position++;
            if (!TryConsumeLineEnding())
            {
                if (position >= script.Length)
                    throw new InvalidOperationException("The string is missing the terminator: " + quote + "@.");
                throw new InvalidOperationException("No characters are allowed after a here-string header but before the end of the line.");
            }

            var contentStart = position;
            var lineStart = true;
            while (position < script.Length)
            {
                if (lineStart &&
                    (IsSingleQuote(quote) ? IsSingleQuote(script[position]) : IsDoubleQuote(script[position])) &&
                    position + 1 < script.Length &&
                    script[position + 1] == '@')
                {
                    var text = TrimFinalLineEnding(script[contentStart..position]);
                    IReadOnlySet<int>? literalDollarOffsets = null;
                    IReadOnlySet<int>? variableBoundaryOffsets = null;
                    if (IsDoubleQuote(quote)) text = DecodeExpandableText(text, out literalDollarOffsets, out variableBoundaryOffsets);
                    position += 2;
                    tokens.Add(new(kind, text, tokenStart, position - tokenStart, leadingWhitespace)
                        { LiteralDollarOffsets = literalDollarOffsets, VariableBoundaryOffsets = variableBoundaryOffsets });
                    leadingWhitespace = false;
                    return;
                }

                if (IsDoubleQuote(quote) && script[position] == '$' && position + 1 < script.Length && script[position + 1] == '(')
                {
                    _ = ReadExpandableSubexpression(script, ref position, hereString: true);
                    lineStart = false;
                }
                else if (IsDoubleQuote(quote) && script[position] == '$' && position + 1 < script.Length && script[position + 1] == '{' &&
                    TryReadVariableName(script, position, out _, out var variableEnd, inExpandableString: true))
                {
                    position = variableEnd;
                    lineStart = false;
                }
                else if (IsDoubleQuote(quote) && script[position] == '`' && position + 1 < script.Length)
                {
                    position += 2;
                    lineStart = false;
                }
                else lineStart = script[position++] is '\r' or '\n';
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

        // Adapt ScanNumberHelper. Keep recognized-but-overflowing literals as Number tokens so the parser can
        // distinguish expression errors from literal command arguments, while lexical nonnumbers stay generic.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L3884-L4162
        void ReadNumber()
        {
            var tokenStart = position;
            if (position + 1 < script.Length && script[position] == '0' && script[position + 1] is 'x' or 'X' or 'b' or 'B')
            {
                var hex = script[position + 1] is 'x' or 'X';
                position += 2;
                while (position < script.Length && (hex ? PowerShellWasmNumericLiteral.IsHexDigit(script[position]) : script[position] is '0' or '1'))
                    position++;
            }
            else
            {
                while (position < script.Length && PowerShellWasmNumericLiteral.IsDecimalDigit(script[position])) position++;
                if (position < script.Length && script[position] == '.' &&
                    (position + 1 == script.Length || script[position + 1] != '.'))
                {
                    position++;
                    while (position < script.Length && PowerShellWasmNumericLiteral.IsDecimalDigit(script[position])) position++;
                }
                if (position < script.Length && script[position] is 'e' or 'E')
                {
                    position++;
                    if (position < script.Length && (script[position] == '+' || PowerShellWasmNumericLiteral.IsDash(script[position]))) position++;
                    while (position < script.Length && PowerShellWasmNumericLiteral.IsDecimalDigit(script[position])) position++;
                }
            }
            if (position < script.Length && IsNumericSuffix(script[position]))
            {
                position++;
                if (position < script.Length && IsNumericSuffix(script[position])) position++;
            }
            if (position < script.Length && char.ToLowerInvariant(script[position]) is 'k' or 'm' or 'g' or 't' or 'p')
            {
                position++;
                if (position < script.Length && script[position] is 'b' or 'B') position++;
            }
            if (!PowerShellWasmNumericLiteral.IsNumericSyntax(script[tokenStart..position]) ||
                position < script.Length && !EndsNumber(script[position]))
            {
                var prefixEnd = position;
                position = tokenStart;
                ReadIdentifier(prefixEnd);
                return;
            }
            tokens.Add(new(PowerShellWasmTokenKind.Number, script[tokenStart..position], tokenStart, position - tokenStart, leadingWhitespace));
            leadingWhitespace = false;
        }

        // ScanGenericToken decodes escapes before treating characters as syntax. Keep fragments separate here;
        // the parser selects expression versus command-argument concatenation without losing source offsets.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L3382-L3468
        void ReadIdentifier(int? numericPrefixEnd = null)
        {
            var tokenStart = position;
            var value = new StringBuilder();
            var dotted = numericPrefixEnd is null && script[position] == '.';
            var escapedText = false;
            HashSet<int>? literalDollarOffsets = null;
            HashSet<int>? variableBoundaryOffsets = null;
            if (numericPrefixEnd is int end)
            {
                value.Append(script.AsSpan(position, end - position));
                position = end;
            }
            else if (dotted) value.Append(script[position++]);
            while (position < script.Length)
            {
                var current = script[position];
                if (current == '`')
                {
                    position++;
                    escapedText = true;
                    var escaped = position < script.Length ? ReadEscape(script, ref position) : "`";
                    (variableBoundaryOffsets ??= []).Add(value.Length);
                    if (escaped == "$") (literalDollarOffsets ??= []).Add(value.Length);
                    value.Append(escaped);
                    continue;
                }
                if (!(dotted ? IsDottedMemberCharacter(current) : IsBareWordCharacter(current))) break;
                value.Append(current);
                position++;
            }

            if (position == tokenStart)
            {
                value.Append(script[position++]);
            }

            tokens.Add(new(PowerShellWasmTokenKind.Identifier, value.ToString(), tokenStart, position - tokenStart, leadingWhitespace)
                { LiteralDollarOffsets = literalDollarOffsets, VariableBoundaryOffsets = variableBoundaryOffsets, IsEscapedText = escapedText });
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
                if (current == '`' && position < text.Length)
                {
                    builder.Append(ReadEscape(text, ref position));
                    continue;
                }
                else if (current == '{') throw new InvalidOperationException("An opening brace in a variable name must be escaped with a backtick.");
                else if (IsDoubleQuote(current) && inExpandableString && position < text.Length && IsDoubleQuote(text[position]))
                    current = text[position++];
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

    // Adapt ScanSubExpression: raw parentheses delimit interpolation even inside nested quotes/comments.
    // Ordinary strings normalize escaped/doubled double quotes once; here-strings preserve the nested script verbatim.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2362-L2446
    private static string ReadExpandableSubexpression(string text, ref int position, bool hereString)
    {
        var value = new StringBuilder("$(");
        position += 2;
        var depth = 1;
        while (position < text.Length)
        {
            var current = text[position++];
            if (current == '(') depth++;
            else if (current == ')') depth--;
            else if (!hereString && (current == '`' || IsDoubleQuote(current)) &&
                position < text.Length && IsDoubleQuote(text[position]))
                current = text[position++];
            value.Append(current);
            if (depth == 0) return value.ToString();
        }
        throw new InvalidOperationException("Missing closing ')' in subexpression.");
    }

    // Adapt ScanHereStringExpandable: outer escapes must not decode text inside a nested expression.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2804-L2839
    private static string DecodeExpandableText(string text, out IReadOnlySet<int>? literalDollarOffsets,
        out IReadOnlySet<int>? variableBoundaryOffsets)
    {
        var value = new StringBuilder();
        HashSet<int>? escapedDollars = null;
        HashSet<int>? escapedCharacters = null;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '$' && index + 1 < text.Length && text[index + 1] == '(')
            {
                value.Append(ReadExpandableSubexpression(text, ref index, hereString: true));
                index--;
            }
            else if (current == '$' && index + 1 < text.Length && text[index + 1] == '{' &&
                TryReadVariableName(text, index, out _, out var variableEnd, inExpandableString: true))
            {
                value.Append(text.AsSpan(index, variableEnd - index));
                index = variableEnd - 1;
            }
            else if (current == '`' && index + 1 < text.Length)
            {
                index++;
                var escaped = ReadEscape(text, ref index);
                index--;
                (escapedCharacters ??= []).Add(value.Length);
                if (escaped == "$") (escapedDollars ??= []).Add(value.Length);
                value.Append(escaped);
            }
            else value.Append(current);
        }
        literalDollarOffsets = escapedDollars;
        variableBoundaryOffsets = escapedCharacters;
        return value.ToString();
    }

    // Behavioral adaptation of Backtick / ScanUnicodeEscape. No platform APIs or native PowerShell dependency.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L1463-L1588
    private static string ReadEscape(string text, ref int position)
    {
        var escaped = text[position++];
        if (escaped != 'u') return ReadEscapedCharacter(escaped).ToString();
        if (position >= text.Length || text[position++] != '{')
            throw new InvalidOperationException("The Unicode escape sequence must use '`u{...}' with one to six hexadecimal digits.");
        var value = 0;
        var digits = 0;
        while (position < text.Length && text[position] != '}')
        {
            var current = text[position++];
            var digit = current switch
            {
                >= '0' and <= '9' => current - '0',
                >= 'a' and <= 'f' => current - 'a' + 10,
                >= 'A' and <= 'F' => current - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || ++digits > 6)
                throw new InvalidOperationException("The Unicode escape sequence must contain one to six hexadecimal digits.");
            value = value * 16 + digit;
        }
        if (digits == 0 || position >= text.Length || text[position++] != '}')
            throw new InvalidOperationException("The Unicode escape sequence is missing hexadecimal digits or its closing '}'.");
        if (value > 0x10ffff) throw new InvalidOperationException("The Unicode escape sequence value is outside the range 0 to 0x10FFFF.");
        return value <= char.MaxValue ? ((char)value).ToString() : char.ConvertFromUtf32(value);
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
            'v' => '\v',
            _ => ch
        };

    private static bool IsBareWordCharacter(char ch) =>
        !char.IsWhiteSpace(ch) && ch is not ';' and not '|' and not '=' and not ',' and not '(' and not ')' and not '{' and not '}'
            and not '[' and not ']' and not '+' and not '*' and not '/' and not '@' and not '$' && !IsQuote(ch);

    private static bool IsDottedMemberCharacter(char ch) =>
        IsBareWordCharacter(ch) && ch is not '.' and not '-' and not '?' and not '%' and not '$';

    private static bool IsNumericSuffix(char ch) => char.ToLowerInvariant(ch) is 'y' or 's' or 'u' or 'l' or 'd' or 'n';
    private static bool EndsNumber(char ch) => char.IsWhiteSpace(ch) || PowerShellWasmNumericLiteral.IsDash(ch) ||
        ch is ';' or '|' or '&' or '(' or ')' or '{' or '}' or ',' or ']' or '!' or '#' or '%' or '*' or '+' or '.' or '/' or
            '=' or '<' or '>' or '?' or ':';

    // PowerShell treats straight and typographic quotes as members of the same delimiter class.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/CharTraits.cs#L263-L282
    private static bool IsSingleQuote(char ch) => ch is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b';
    private static bool IsDoubleQuote(char ch) => ch is '"' or '\u201c' or '\u201d' or '\u201e';
    private static bool IsQuote(char ch) => IsSingleQuote(ch) || IsDoubleQuote(ch);
}
