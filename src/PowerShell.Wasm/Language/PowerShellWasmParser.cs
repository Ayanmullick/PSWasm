using System.Globalization;

namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/Parser.cs
// Browser note: this parser models a browser-safe PowerShell subset and produces the PSWasm AST profile.
public sealed class PowerShellWasmParser
{
    public ScriptAst Parse(string script)
    {
        var tokens = PowerShellWasmTokenizer.Tokenize(script);
        return new ScriptAst(ParseStatements(tokens));
    }

    private static IReadOnlyList<StatementAst> ParseStatements(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var statements = new List<StatementAst>();
        var position = 0;

        while (position < tokens.Count && tokens[position].Kind != PowerShellWasmTokenKind.EndOfInput)
        {
            SkipStatementSeparators(tokens, ref position);
            if (position >= tokens.Count || tokens[position].Kind == PowerShellWasmTokenKind.EndOfInput)
            {
                break;
            }

            var statementTokens = ReadStatementTokens(tokens, ref position);
            if (statementTokens.Count > 0)
            {
                statements.Add(ParseStatement(statementTokens));
            }
        }

        return statements;
    }

    private static StatementAst ParseStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        if (IsKeyword(tokens, 0, "try"))
        {
            return ParseTryStatement(tokens);
        }

        var pipelineChain = SplitTopLevelPipelineChain(tokens);
        if (pipelineChain.Segments.Count > 1)
        {
            var first = ParseStatement(pipelineChain.Segments[0]);
            var clauses = pipelineChain.Operators.Select((op, index) =>
                new PipelineChainClauseAst(op, ParseStatement(pipelineChain.Segments[index + 1]))).ToArray();
            return new PipelineChainStatementAst(first, clauses);
        }

        var pipelineSegments = SplitTopLevel(tokens, PowerShellWasmTokenKind.Pipe);
        if (pipelineSegments.Count > 1)
        {
            return new PipelineStatementAst(pipelineSegments.Select(ParsePipelineElement).ToArray());
        }

        var equals = FindTopLevel(tokens, PowerShellWasmTokenKind.Equals);
        if (tokens.Count > 0 && tokens[0].Kind == PowerShellWasmTokenKind.Variable && equals > 0)
        {
            return new AssignmentStatementAst(tokens[0].Text, ParseExpression(tokens.Skip(equals + 1).ToArray()));
        }

        if (IsCommandSegment(tokens))
        {
            return new CommandStatementAst(ParseCommand(tokens));
        }

        return new ExpressionStatementAst(ParseExpression(tokens));
    }

    private static TryStatementAst ParseTryStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var tryBlock = ReadScriptBlock(tokens, ref position, "try");
        var catchBlocks = new List<ScriptAst>();
        ScriptAst? finallyBlock = null;

        while (position < tokens.Count)
        {
            if (IsKeyword(tokens, position, "catch"))
            {
                position++;
                while (position < tokens.Count && tokens[position].Kind != PowerShellWasmTokenKind.LBrace)
                {
                    position++;
                }

                catchBlocks.Add(ReadScriptBlock(tokens, ref position, "catch"));
                continue;
            }

            if (IsKeyword(tokens, position, "finally"))
            {
                position++;
                finallyBlock = ReadScriptBlock(tokens, ref position, "finally");
                break;
            }

            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' in try statement.");
        }

        if (catchBlocks.Count == 0 && finallyBlock is null)
        {
            throw new InvalidOperationException("A try statement requires at least one catch or finally block.");
        }

        return new TryStatementAst(tryBlock, catchBlocks, finallyBlock);
    }

    private static ScriptAst ReadScriptBlock(IReadOnlyList<PowerShellWasmToken> tokens, ref int position, string blockName)
    {
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.LBrace)
        {
            throw new InvalidOperationException($"Expected '{{' after {blockName}.");
        }

        position++;
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RBrace && depth == 0)
            {
                var bodyTokens = tokens.Skip(start).Take(position - start).ToArray();
                position++;
                return new ScriptAst(ParseStatements(bodyTokens));
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException($"Expected '}}' to close {blockName}.");
    }

    private static PipelineElementAst ParsePipelineElement(IReadOnlyList<PowerShellWasmToken> tokens) =>
        IsCommandSegment(tokens)
            ? new CommandPipelineElementAst(ParseCommand(tokens))
            : new ExpressionPipelineElementAst(ParseExpression(tokens));

    private static (IReadOnlyList<IReadOnlyList<PowerShellWasmToken>> Segments, IReadOnlyList<PipelineChainOperator> Operators)
        SplitTopLevelPipelineChain(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var segments = new List<IReadOnlyList<PowerShellWasmToken>>();
        var operators = new List<PipelineChainOperator>();
        var start = 0;
        var depth = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (depth == 0 && tokens[i].Kind is PowerShellWasmTokenKind.PipelineChainAnd or PowerShellWasmTokenKind.PipelineChainOr)
            {
                segments.Add(tokens.Skip(start).Take(i - start).ToArray());
                operators.Add(tokens[i].Kind == PowerShellWasmTokenKind.PipelineChainAnd
                    ? PipelineChainOperator.And
                    : PipelineChainOperator.Or);
                start = i + 1;
                continue;
            }

            UpdateDepth(tokens[i], ref depth);
        }

        segments.Add(tokens.Skip(start).ToArray());
        return (segments, operators);
    }

    private static CommandAst ParseCommand(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        if (tokens.Count == 0 || tokens[0].Kind != PowerShellWasmTokenKind.Identifier)
        {
            throw new InvalidOperationException("Expected a command name.");
        }

        var parameters = new List<CommandParameterAst>();
        var arguments = new List<CommandArgumentAst>();
        var position = 1;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.Parameter)
            {
                var name = tokens[position++].Text;
                if (position >= tokens.Count || tokens[position].Kind == PowerShellWasmTokenKind.Parameter || IsSplatStart(tokens, position))
                {
                    parameters.Add(new(name, null));
                    continue;
                }

                parameters.Add(new(name, ParseCommandArgumentExpression(ReadCommandArgument(tokens, ref position))));
                continue;
            }

            if (IsSplatStart(tokens, position))
            {
                var name = tokens[position + 1].Text;
                arguments.Add(new CommandArgumentAst(new VariableExpressionAst(name, false), IsSplat: true));
                position += 2;
                continue;
            }

            arguments.Add(new CommandArgumentAst(ParseCommandArgumentExpression(ReadCommandArgument(tokens, ref position))));
        }

        return new CommandAst(tokens[0].Text, parameters, arguments);
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadCommandArgument(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var start = position;
        if (tokens[position].Kind is PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or
            PowerShellWasmTokenKind.AtLBrace or PowerShellWasmTokenKind.AtLParen or PowerShellWasmTokenKind.LBracket)
        {
            var depth = 0;
            do
            {
                UpdateDepth(tokens[position], ref depth);
                position++;
            }
            while (position < tokens.Count && depth > 0);

            return tokens.Skip(start).Take(position - start).ToArray();
        }

        if (tokens[position].Kind is PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus && position + 1 < tokens.Count)
        {
            position += 2;
            return tokens.Skip(start).Take(position - start).ToArray();
        }

        if (tokens[position].Kind == PowerShellWasmTokenKind.Star)
        {
            position++;
            while (position < tokens.Count && IsContiguousBareCommandToken(tokens[position - 1], tokens[position]))
            {
                position++;
            }

            return tokens.Skip(start).Take(position - start).ToArray();
        }

        position++;
        while (position < tokens.Count && tokens[position].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.LBracket &&
            !tokens[position].HasLeadingWhitespace)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.Identifier && !tokens[position].Text.StartsWith(".", StringComparison.Ordinal))
            {
                break;
            }

            if (tokens[position].Kind == PowerShellWasmTokenKind.Identifier)
            {
                position++;
                continue;
            }

            var depth = 0;
            do
            {
                UpdateDepth(tokens[position], ref depth);
                position++;
            }
            while (position < tokens.Count && depth > 0);
        }

        if (position < tokens.Count && tokens[position].Kind.IsBinaryOperator())
        {
            while (position < tokens.Count && tokens[position].Kind != PowerShellWasmTokenKind.Parameter && !IsSplatStart(tokens, position))
            {
                position++;
            }
        }

        return tokens.Skip(start).Take(position - start).ToArray();
    }

    private static ExpressionAst ParseCommandArgumentExpression(IReadOnlyList<PowerShellWasmToken> tokens) =>
        IsBareWildcardArgument(tokens)
            ? new BareWordExpressionAst(string.Concat(tokens.Select(static token => token.Text)))
            : ParseExpression(tokens);

    private static bool IsBareWildcardArgument(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 1 &&
        tokens.Any(static token => token.Kind == PowerShellWasmTokenKind.Star) &&
        tokens.All(static token => token.Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Star) &&
        tokens.Zip(tokens.Skip(1)).All(static pair => IsContiguousBareCommandToken(pair.First, pair.Second));

    private static bool IsContiguousBareCommandToken(PowerShellWasmToken left, PowerShellWasmToken right) =>
        !right.HasLeadingWhitespace && right.Offset == left.Offset + left.Length &&
        right.Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Star;

    private static ExpressionAst ParseExpression(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var parser = new ExpressionParser(tokens);
        return parser.Parse();
    }

    private static bool IsCommandSegment(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 0 && tokens[0].Kind == PowerShellWasmTokenKind.Identifier;

    private static bool IsKeyword(IReadOnlyList<PowerShellWasmToken> tokens, int position, string keyword) =>
        position < tokens.Count &&
        tokens[position].Kind == PowerShellWasmTokenKind.Identifier &&
        tokens[position].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsSplatStart(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        position + 1 < tokens.Count &&
        tokens[position].Kind == PowerShellWasmTokenKind.At &&
        tokens[position + 1].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Variable;

    private static void SkipStatementSeparators(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        while (position < tokens.Count && tokens[position].Kind is PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon)
        {
            position++;
        }
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadStatementTokens(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Kind == PowerShellWasmTokenKind.EndOfInput)
            {
                break;
            }

            if (depth == 0 && token.Kind is PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon)
            {
                if (token.Kind == PowerShellWasmTokenKind.NewLine && LastSignificantTokenKind(tokens, start, position) is
                    PowerShellWasmTokenKind.Pipe or PowerShellWasmTokenKind.PipelineChainAnd or PowerShellWasmTokenKind.PipelineChainOr)
                {
                    position++;
                    continue;
                }

                if (IsTryContinuation(tokens, start, position))
                {
                    position++;
                    continue;
                }

                break;
            }

            UpdateDepth(token, ref depth);
            position++;
        }

        return tokens.Skip(start).Take(position - start).Where(static t => t.Kind != PowerShellWasmTokenKind.NewLine).ToArray();
    }

    private static PowerShellWasmTokenKind? LastSignificantTokenKind(
        IReadOnlyList<PowerShellWasmToken> tokens,
        int start,
        int position)
    {
        for (var i = position - 1; i >= start; i--)
        {
            if (tokens[i].Kind is not PowerShellWasmTokenKind.NewLine and not PowerShellWasmTokenKind.Semicolon)
            {
                return tokens[i].Kind;
            }
        }

        return null;
    }

    private static bool IsTryContinuation(IReadOnlyList<PowerShellWasmToken> tokens, int start, int separatorPosition)
    {
        if (!IsKeyword(tokens, start, "try") ||
            LastSignificantTokenKind(tokens, start, separatorPosition) != PowerShellWasmTokenKind.RBrace)
        {
            return false;
        }

        for (var i = separatorPosition + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon)
            {
                continue;
            }

            return IsKeyword(tokens, i, "catch") || IsKeyword(tokens, i, "finally");
        }

        return false;
    }

    private static IReadOnlyList<IReadOnlyList<PowerShellWasmToken>> SplitTopLevel(
        IReadOnlyList<PowerShellWasmToken> tokens,
        PowerShellWasmTokenKind separator)
    {
        var result = new List<IReadOnlyList<PowerShellWasmToken>>();
        var start = 0;
        var depth = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (depth == 0 && tokens[i].Kind == separator)
            {
                result.Add(tokens.Skip(start).Take(i - start).ToArray());
                start = i + 1;
                continue;
            }

            UpdateDepth(tokens[i], ref depth);
        }

        result.Add(tokens.Skip(start).ToArray());
        return result;
    }

    private static int FindTopLevel(IReadOnlyList<PowerShellWasmToken> tokens, PowerShellWasmTokenKind kind)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (depth == 0 && tokens[i].Kind == kind)
            {
                return i;
            }

            UpdateDepth(tokens[i], ref depth);
        }

        return -1;
    }

    private static void UpdateDepth(PowerShellWasmToken token, ref int depth)
    {
        depth += token.Kind switch
        {
            PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or PowerShellWasmTokenKind.AtLBrace or
                PowerShellWasmTokenKind.AtLParen or PowerShellWasmTokenKind.LBracket => 1,
            PowerShellWasmTokenKind.RParen or PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.RBracket => -1,
            _ => 0
        };
    }

    private sealed class ExpressionParser(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        private int _position;

        public ExpressionAst Parse()
        {
            if (tokens.Count == 0)
            {
                return new StringExpressionAst(string.Empty, IsExpandable: false);
            }

            return ParseCommaExpression();
        }

        private ExpressionAst ParseCommaExpression()
        {
            var items = new List<ExpressionAst>();
            var leadingComma = false;

            if (Current.Kind == PowerShellWasmTokenKind.Comma)
            {
                leadingComma = true;
                _position++;
            }

            items.Add(ParseAssignment());
            while (Current.Kind == PowerShellWasmTokenKind.Comma)
            {
                _position++;
                items.Add(ParseAssignment());
            }

            return leadingComma || items.Count > 1 ? new ArrayExpressionAst(items) : items[0];
        }

        private ExpressionAst ParseAssignment()
        {
            if (Current.Kind == PowerShellWasmTokenKind.Variable && Peek(1).Kind == PowerShellWasmTokenKind.Equals)
            {
                var variable = Current.Text;
                _position += 2;
                return new AssignmentExpressionAst(variable, ParseAssignment());
            }

            return ParseBinaryExpression(1);
        }

        private ExpressionAst ParseBinaryExpression(int minimumPrecedence)
        {
            var expression = ParseUnary();
            while (Current.Kind.IsBinaryOperator())
            {
                var precedence = Current.Kind.GetBinaryPrecedence();
                if (precedence < minimumPrecedence)
                {
                    break;
                }

                _position++;
                var op = MapBinaryOperator(tokens[_position - 1].Kind);
                expression = new BinaryExpressionAst(expression, op, ParseBinaryExpression(precedence + 1));
            }

            return expression;
        }

        private ExpressionAst ParseUnary()
        {
            if (Current.Kind.IsUnaryOperator())
            {
                var op = MapUnaryOperator(Current.Kind);
                _position++;
                return new UnaryExpressionAst(op, ParseUnary());
            }

            return ParsePostfix();
        }

        private ExpressionAst ParsePostfix()
        {
            var expression = ParsePrimary();
            while (Current.Kind == PowerShellWasmTokenKind.Identifier &&
                Current.Text.StartsWith(".", StringComparison.Ordinal) &&
                Current.Text.Length > 1)
            {
                expression = new MemberAccessExpressionAst(expression, Current.Text[1..]);
                _position++;
            }

            while (Current.Kind == PowerShellWasmTokenKind.LBracket)
            {
                expression = ParseIndex(expression);
            }

            return expression;
        }

        private ExpressionAst ParsePrimary()
        {
            var token = Current;
            _position++;

            return token.Kind switch
            {
                PowerShellWasmTokenKind.Number => ParseNumber(token.Text),
                PowerShellWasmTokenKind.StringLiteral => new StringExpressionAst(token.Text, IsExpandable: false),
                PowerShellWasmTokenKind.ExpandableStringLiteral => new StringExpressionAst(token.Text, IsExpandable: true),
                PowerShellWasmTokenKind.Variable => ParseVariable(token.Text),
                PowerShellWasmTokenKind.Identifier => new BareWordExpressionAst(token.Text),
                PowerShellWasmTokenKind.Parameter => new BareWordExpressionAst("-" + token.Text),
                PowerShellWasmTokenKind.LParen => ParseParenthesized(),
                PowerShellWasmTokenKind.LBrace => ParseScriptBlock(),
                PowerShellWasmTokenKind.AtLBrace => ParseHashtable(),
                PowerShellWasmTokenKind.AtLParen => ParseArray(),
                _ => new BareWordExpressionAst(token.Text)
            };
        }

        private ExpressionAst ParseParenthesized()
        {
            var expression = ParseAssignment();
            Consume(PowerShellWasmTokenKind.RParen);
            return new ParenthesizedExpressionAst(expression);
        }

        private IndexExpressionAst ParseIndex(ExpressionAst target)
        {
            Consume(PowerShellWasmTokenKind.LBracket);
            var indexes = new List<ExpressionAst>();
            while (Current.Kind is not PowerShellWasmTokenKind.RBracket and not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.Comma)
                {
                    _position++;
                    continue;
                }

                indexes.Add(ParseAssignment());
                if (Current.Kind == PowerShellWasmTokenKind.Comma)
                {
                    _position++;
                }
            }

            Consume(PowerShellWasmTokenKind.RBracket);
            var index = indexes.Count == 1 ? indexes[0] : new ArrayExpressionAst(indexes);
            return new IndexExpressionAst(target, index);
        }

        private ScriptBlockExpressionAst ParseScriptBlock()
        {
            var start = _position;
            var depth = 0;

            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.RBrace && depth == 0)
                {
                    break;
                }

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var bodyTokens = tokens.Skip(start).Take(_position - start).ToArray();
            Consume(PowerShellWasmTokenKind.RBrace);
            return new ScriptBlockExpressionAst(new ScriptAst(ParseStatements(bodyTokens)));
        }

        private HashtableExpressionAst ParseHashtable()
        {
            var entries = new List<HashtableEntryAst>();
            while (Current.Kind is not PowerShellWasmTokenKind.RBrace and not PowerShellWasmTokenKind.EndOfInput)
            {
                SkipExpressionSeparators();
                if (Current.Kind is PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.EndOfInput)
                {
                    break;
                }

                var key = ReadHashtableKey();
                Consume(PowerShellWasmTokenKind.Equals);
                entries.Add(new(key, ParseAssignment()));
                SkipExpressionSeparators();
            }

            Consume(PowerShellWasmTokenKind.RBrace);
            return new HashtableExpressionAst(entries);
        }

        private ArrayExpressionAst ParseArray()
        {
            var items = new List<ExpressionAst>();
            while (Current.Kind is not PowerShellWasmTokenKind.RParen and not PowerShellWasmTokenKind.EndOfInput)
            {
                SkipExpressionSeparators();
                if (Current.Kind is PowerShellWasmTokenKind.RParen or PowerShellWasmTokenKind.EndOfInput)
                {
                    break;
                }

                items.Add(ParseAssignment());
                SkipExpressionSeparators();
            }

            Consume(PowerShellWasmTokenKind.RParen);
            return new ArrayExpressionAst(items);
        }

        private string ReadHashtableKey()
        {
            var token = Current;
            _position++;
            return token.Text;
        }

        private void SkipExpressionSeparators()
        {
            while (Current.Kind is PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.Semicolon or PowerShellWasmTokenKind.NewLine)
            {
                _position++;
            }
        }

        private void Consume(PowerShellWasmTokenKind kind)
        {
            if (Current.Kind == kind)
            {
                _position++;
                return;
            }

            throw new InvalidOperationException($"Expected token {kind}, found {Current.Kind}.");
        }

        private PowerShellWasmToken Current =>
            _position < tokens.Count ? tokens[_position] : new(PowerShellWasmTokenKind.EndOfInput, string.Empty, 0, 0, false);

        private PowerShellWasmToken Peek(int offset)
        {
            var position = _position + offset;
            return position < tokens.Count ? tokens[position] : new(PowerShellWasmTokenKind.EndOfInput, string.Empty, 0, 0, false);
        }

        private static ExpressionAst ParseNumber(string text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                ? new NumberExpressionAst(intValue)
                : new NumberExpressionAst(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));

        private static VariableExpressionAst ParseVariable(string text)
        {
            if (text.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                return new VariableExpressionAst(text[4..], IsEnvironment: true);
            }

            return new VariableExpressionAst(text, IsEnvironment: false);
        }

        private static PowerShellWasmUnaryOperator MapUnaryOperator(PowerShellWasmTokenKind kind) =>
            kind switch
            {
                PowerShellWasmTokenKind.Plus => PowerShellWasmUnaryOperator.Plus,
                PowerShellWasmTokenKind.Minus => PowerShellWasmUnaryOperator.Minus,
                PowerShellWasmTokenKind.Not => PowerShellWasmUnaryOperator.Not,
                PowerShellWasmTokenKind.Bnot => PowerShellWasmUnaryOperator.BitwiseNot,
                PowerShellWasmTokenKind.Join => PowerShellWasmUnaryOperator.Join,
                PowerShellWasmTokenKind.Isplit => PowerShellWasmUnaryOperator.Split,
                PowerShellWasmTokenKind.Csplit => PowerShellWasmUnaryOperator.CaseSensitiveSplit,
                _ => throw new InvalidOperationException($"Token {kind} is not a unary operator.")
            };

        private static PowerShellWasmBinaryOperator MapBinaryOperator(PowerShellWasmTokenKind kind) =>
            kind switch
            {
                PowerShellWasmTokenKind.Plus => PowerShellWasmBinaryOperator.Add,
                PowerShellWasmTokenKind.Minus => PowerShellWasmBinaryOperator.Subtract,
                PowerShellWasmTokenKind.Star => PowerShellWasmBinaryOperator.Multiply,
                PowerShellWasmTokenKind.Slash => PowerShellWasmBinaryOperator.Divide,
                PowerShellWasmTokenKind.Remainder => PowerShellWasmBinaryOperator.Remainder,
                PowerShellWasmTokenKind.DotDot => PowerShellWasmBinaryOperator.Range,
                PowerShellWasmTokenKind.Format => PowerShellWasmBinaryOperator.Format,
                PowerShellWasmTokenKind.And => PowerShellWasmBinaryOperator.LogicalAnd,
                PowerShellWasmTokenKind.Or => PowerShellWasmBinaryOperator.LogicalOr,
                PowerShellWasmTokenKind.Xor => PowerShellWasmBinaryOperator.LogicalXor,
                PowerShellWasmTokenKind.Band => PowerShellWasmBinaryOperator.BitwiseAnd,
                PowerShellWasmTokenKind.Bor => PowerShellWasmBinaryOperator.BitwiseOr,
                PowerShellWasmTokenKind.Bxor => PowerShellWasmBinaryOperator.BitwiseXor,
                PowerShellWasmTokenKind.Join => PowerShellWasmBinaryOperator.Join,
                PowerShellWasmTokenKind.Isplit => PowerShellWasmBinaryOperator.Split,
                PowerShellWasmTokenKind.Csplit => PowerShellWasmBinaryOperator.CaseSensitiveSplit,
                PowerShellWasmTokenKind.Shl => PowerShellWasmBinaryOperator.ShiftLeft,
                PowerShellWasmTokenKind.Shr => PowerShellWasmBinaryOperator.ShiftRight,
                PowerShellWasmTokenKind.QuestionQuestion => PowerShellWasmBinaryOperator.NullCoalesce,
                PowerShellWasmTokenKind.Ieq => PowerShellWasmBinaryOperator.Equal,
                PowerShellWasmTokenKind.Ine => PowerShellWasmBinaryOperator.NotEqual,
                PowerShellWasmTokenKind.Ige => PowerShellWasmBinaryOperator.GreaterThanOrEqual,
                PowerShellWasmTokenKind.Igt => PowerShellWasmBinaryOperator.GreaterThan,
                PowerShellWasmTokenKind.Ilt => PowerShellWasmBinaryOperator.LessThan,
                PowerShellWasmTokenKind.Ile => PowerShellWasmBinaryOperator.LessThanOrEqual,
                PowerShellWasmTokenKind.Ilike => PowerShellWasmBinaryOperator.Like,
                PowerShellWasmTokenKind.Inotlike => PowerShellWasmBinaryOperator.NotLike,
                PowerShellWasmTokenKind.Imatch => PowerShellWasmBinaryOperator.Match,
                PowerShellWasmTokenKind.Inotmatch => PowerShellWasmBinaryOperator.NotMatch,
                PowerShellWasmTokenKind.Ireplace => PowerShellWasmBinaryOperator.Replace,
                PowerShellWasmTokenKind.Icontains => PowerShellWasmBinaryOperator.Contains,
                PowerShellWasmTokenKind.Inotcontains => PowerShellWasmBinaryOperator.NotContains,
                PowerShellWasmTokenKind.Iin => PowerShellWasmBinaryOperator.In,
                PowerShellWasmTokenKind.Inotin => PowerShellWasmBinaryOperator.NotIn,
                PowerShellWasmTokenKind.Ceq => PowerShellWasmBinaryOperator.CaseSensitiveEqual,
                PowerShellWasmTokenKind.Cne => PowerShellWasmBinaryOperator.CaseSensitiveNotEqual,
                PowerShellWasmTokenKind.Cge => PowerShellWasmBinaryOperator.CaseSensitiveGreaterThanOrEqual,
                PowerShellWasmTokenKind.Cgt => PowerShellWasmBinaryOperator.CaseSensitiveGreaterThan,
                PowerShellWasmTokenKind.Clt => PowerShellWasmBinaryOperator.CaseSensitiveLessThan,
                PowerShellWasmTokenKind.Cle => PowerShellWasmBinaryOperator.CaseSensitiveLessThanOrEqual,
                PowerShellWasmTokenKind.Clike => PowerShellWasmBinaryOperator.CaseSensitiveLike,
                PowerShellWasmTokenKind.Cnotlike => PowerShellWasmBinaryOperator.CaseSensitiveNotLike,
                PowerShellWasmTokenKind.Cmatch => PowerShellWasmBinaryOperator.CaseSensitiveMatch,
                PowerShellWasmTokenKind.Cnotmatch => PowerShellWasmBinaryOperator.CaseSensitiveNotMatch,
                PowerShellWasmTokenKind.Creplace => PowerShellWasmBinaryOperator.CaseSensitiveReplace,
                PowerShellWasmTokenKind.Ccontains => PowerShellWasmBinaryOperator.CaseSensitiveContains,
                PowerShellWasmTokenKind.Cnotcontains => PowerShellWasmBinaryOperator.CaseSensitiveNotContains,
                PowerShellWasmTokenKind.Cin => PowerShellWasmBinaryOperator.CaseSensitiveIn,
                PowerShellWasmTokenKind.Cnotin => PowerShellWasmBinaryOperator.CaseSensitiveNotIn,
                _ => throw new InvalidOperationException($"Token {kind} is not a binary operator.")
            };
    }
}
