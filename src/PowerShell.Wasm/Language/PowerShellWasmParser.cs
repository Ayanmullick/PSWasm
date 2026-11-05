using System.Globalization;

namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/Parser.cs
// Ternary reference: ExpressionRule handling for QuestionMark / Colon and TernaryExpressionAst construction.
// Null-conditional member reference: member-access parsing for the QuestionDot token.
// Null coalescing assignment reference: compound assignment parsing for the QuestionQuestion token followed by Equals.
// Browser note: this parser models a browser-safe PowerShell subset and produces the PSWasm AST profile.
public sealed class PowerShellWasmParser
{
    public ScriptAst Parse(string script) => Parse(script, insideCatch: false);

    internal ScriptAst Parse(string script, bool insideCatch, bool insideFinally = false)
    {
        IReadOnlyList<PowerShellWasmToken> tokens = PowerShellWasmTokenizer.Tokenize(script)
            .Select(token => token with { SourceScript = script }).ToArray();
        if (insideCatch) tokens = MarkInsideCatch(tokens);
        if (insideFinally) tokens = MarkInsideFinally(tokens, true);
        return ParseScript(tokens);
    }

    // A bare throw is a rethrow only when lexically within a catch, including nested function/scriptblock definitions.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/ast.cs#L5468-L5498
    // Token copies carry that ancestry into nested parses; strings retain it for their later-parsed $() expressions.
    private static IReadOnlyList<PowerShellWasmToken> MarkInsideCatch(IEnumerable<PowerShellWasmToken> tokens) =>
        tokens.Select(static token => token with { IsInCatch = true }).ToArray();

    // Return cannot leave a finally block; a nested function/scriptblock starts its own return boundary.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/SemanticChecks.cs#L630-L665
    private static IReadOnlyList<PowerShellWasmToken> MarkInsideFinally(IEnumerable<PowerShellWasmToken> tokens, bool value) =>
        tokens.Select(token => token with { IsInFinally = value }).ToArray();

    private static StringExpressionAst ParseStringToken(PowerShellWasmToken token)
    {
        var expression = new StringExpressionAst(token.Text, token.Kind == PowerShellWasmTokenKind.ExpandableStringLiteral)
            { LiteralDollarOffsets = token.LiteralDollarOffsets, VariableBoundaryOffsets = token.VariableBoundaryOffsets,
                IsInCatch = token.IsInCatch, IsInFinally = token.IsInFinally };
        if (!expression.IsExpandable) return expression;

        // Native parsing and semantic validation visit interpolation ASTs even in unreachable code.
        for (var index = 0; index < token.Text.Length; index++)
        {
            if (token.Text[index] != '$' || token.LiteralDollarOffsets?.Contains(index) == true) continue;
            if (TryReadExpandableSubexpression(token.Text, index, out var script, out var nextIndex))
            {
                _ = new PowerShellWasmParser().Parse(script, token.IsInCatch, token.IsInFinally);
                index = nextIndex - 1;
            }
            else if (PowerShellWasmTokenizer.TryReadVariableName(token.Text, index, out _, out var variableEnd,
                inExpandableString: true, token.VariableBoundaryOffsets)) index = variableEnd - 1;
        }
        return expression;
    }

    internal static bool TryReadExpandableSubexpression(string value, int start, out string script, out int nextIndex)
    {
        script = string.Empty;
        nextIndex = start;
        if (start + 1 >= value.Length || value[start + 1] != '(') return false;

        var expressionStart = start + 2;
        var depth = 1;
        // Native string interpolation balances raw parentheses before parsing the nested code, including in comments/quotes.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L2362-L2446
        for (var index = expressionStart; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '(') { depth++; continue; }
            if (ch != ')' || --depth != 0) continue;
            script = value[expressionStart..index];
            nextIndex = index + 1;
            return true;
        }
        return false;
    }

    private static ScriptAst ParseScript(IReadOnlyList<PowerShellWasmToken> tokens, bool allowParamBlock = true)
    {
        var statements = ParseStatements(tokens);
        var contentStart = statements.FindIndex(static statement => statement is not MetadataAttributeStatementAst);
        if (contentStart < 0)
        {
            return new ScriptAst([]);
        }

        if (contentStart > 0)
        {
            statements = statements.Skip(contentStart).ToList();
        }

        var paramBlockIndex = statements.FindIndex(static statement => statement is ParamBlockStatementAst);
        if (paramBlockIndex < 0)
        {
            return new ScriptAst(statements);
        }

        if (!allowParamBlock)
        {
            throw new InvalidOperationException("Function parameters must be declared either after the function name or in a param block, not both.");
        }

        if (paramBlockIndex > 0)
        {
            throw new InvalidOperationException("A param block must be the first statement in a script or script block.");
        }

        if (statements.Skip(1).Any(static statement => statement is ParamBlockStatementAst))
        {
            throw new InvalidOperationException("Only one param block is supported in a script or script block.");
        }

        var paramBlock = (ParamBlockStatementAst)statements[0];
        return new ScriptAst(statements.Skip(1).ToArray(), paramBlock.Parameters);
    }

    private static List<StatementAst> ParseStatements(IReadOnlyList<PowerShellWasmToken> tokens)
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

    private static StatementAst ParseStatement(IReadOnlyList<PowerShellWasmToken> tokens, bool requireCompleteExpressions = false)
    {
        ExpressionAst ParseValue(IReadOnlyList<PowerShellWasmToken> valueTokens) => requireCompleteExpressions
            ? new ExpressionParser(valueTokens).Parse(requireComplete: true) : ParseExpression(valueTokens);

        if (IsKeyword(tokens, 0, "try"))
        {
            return ParseTryStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "if"))
        {
            return ParseIfStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "foreach"))
        {
            return ParseForEachStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "while"))
        {
            return ParseWhileStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "do"))
        {
            return ParseDoWhileStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "for"))
        {
            return ParseForStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "switch"))
        {
            return ParseSwitchStatement(tokens);
        }

        if (IsKeyword(tokens, 0, "function"))
        {
            return ParseFunctionDefinition(tokens);
        }

        if (IsKeyword(tokens, 0, "param") && CurrentKind(tokens, 1) == PowerShellWasmTokenKind.LParen)
        {
            return ParseParamBlock(tokens);
        }

        if (TryParseMetadataAttributeStatement(tokens, out var metadataAttribute))
        {
            return metadataAttribute;
        }

        if (IsKeyword(tokens, 0, "throw"))
        {
            // Throw owns the complete following pipeline-chain, rather than receiving a command's argument list.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L2286-L2296
            return new ThrowStatementAst(tokens.Count == 1 ? null : ParseParenthesizedExpressionValue(tokens.Skip(1).ToArray()))
                { IsRethrow = tokens.Count == 1 && tokens[0].IsInCatch };
        }

        if (IsKeyword(tokens, 0, "return"))
        {
            if (tokens[0].IsInFinally)
                throw new InvalidOperationException("Flow of control cannot leave a Finally block.");
            // Return owns a pipeline-chain, including command invocation and assignment; explicit grouping remains distinct.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L2262-L2271
            return new ReturnStatementAst(tokens.Count == 1 ? null : ParseParenthesizedExpressionValue(tokens.Skip(1).ToArray()));
        }

        if (IsKeyword(tokens, 0, "break"))
        {
            if (tokens.Count > 1)
            {
                throw new InvalidOperationException("The browser-safe break statement does not accept arguments.");
            }

            return new BreakStatementAst();
        }

        if (IsKeyword(tokens, 0, "continue"))
        {
            if (tokens.Count > 1)
            {
                throw new InvalidOperationException("The browser-safe continue statement does not accept arguments.");
            }

            return new ContinueStatementAst();
        }

        if (TryParseCompoundAssignmentStatement(tokens, out var compoundAssignment))
        {
            return compoundAssignment;
        }

        if (TryParseSettableCompoundAssignmentStatement(tokens, out var settableCompoundAssignment))
        {
            return settableCompoundAssignment;
        }

        var equals = FindAssignmentEquals(tokens);
        // Assignment targets can start with an expression, e.g. (Get-ItemValue).Name; their statements emit no value.
        if (equals > 0 && !IsCommandSegment(tokens))
        {
            var valueTokens = tokens.Skip(equals + 1).ToArray();
            if (TryReadAssignmentTargets(tokens, equals, out var variableNames))
            {
                if (variableNames.Count == 1)
                {
                    return IsStatementAssignmentValue(valueTokens)
                        ? new StatementAssignmentAst(variableNames[0], ParseStatement(valueTokens, requireCompleteExpressions))
                        : new AssignmentStatementAst(variableNames[0], ParseValue(valueTokens));
                }

                return IsStatementAssignmentValue(valueTokens)
                    ? new ParallelStatementAssignmentAst(variableNames, ParseStatement(valueTokens, requireCompleteExpressions))
                    : new ParallelAssignmentStatementAst(variableNames, ParseValue(valueTokens));
            }

            var target = ParseExpression(tokens.Take(equals).ToArray());
            if (!IsSimpleAssignmentTarget(target))
            {
                throw new InvalidOperationException("Expected one or more assignment targets before '='.");
            }

            return IsStatementAssignmentValue(valueTokens)
                ? new SettableStatementAssignmentAst(target, ParseStatement(valueTokens, requireCompleteExpressions))
                : new SettableAssignmentStatementAst(target, ParseValue(valueTokens));
        }

        var pipelineChain = SplitTopLevelPipelineChain(tokens);
        if (pipelineChain.Segments.Count > 1)
        {
            var first = ParseStatement(pipelineChain.Segments[0], requireCompleteExpressions);
            var clauses = pipelineChain.Operators.Select((op, index) =>
                new PipelineChainClauseAst(op, ParseStatement(pipelineChain.Segments[index + 1], requireCompleteExpressions))).ToArray();
            return new PipelineChainStatementAst(first, clauses);
        }

        var pipelineSegments = SplitTopLevel(tokens, PowerShellWasmTokenKind.Pipe);
        if (pipelineSegments.Count > 1)
        {
            return new PipelineStatementAst(pipelineSegments.Select(segment => requireCompleteExpressions && !IsCommandSegment(segment)
                ? (PipelineElementAst)new ExpressionPipelineElementAst(ParseValue(segment)) : ParsePipelineElement(segment)).ToArray());
        }

        if (IsCommandSegment(tokens))
        {
            return new CommandStatementAst(ParseCommand(tokens));
        }

        return new ExpressionStatementAst(ParseValue(tokens));
    }

    private static IfStatementAst ParseIfStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var clauses = new List<IfClauseAst>
        {
            new(ReadParenthesizedExpression(tokens, ref position, "if"), ReadScriptBlock(tokens, ref position, "if"))
        };
        ScriptAst? elseBlock = null;

        while (position < tokens.Count)
        {
            SkipStatementSeparators(tokens, ref position);
            if (position >= tokens.Count)
            {
                break;
            }

            if (IsKeyword(tokens, position, "elseif"))
            {
                position++;
                clauses.Add(new(ReadParenthesizedExpression(tokens, ref position, "elseif"),
                    ReadScriptBlock(tokens, ref position, "elseif")));
                continue;
            }

            if (IsKeyword(tokens, position, "else"))
            {
                position++;
                elseBlock = ReadScriptBlock(tokens, ref position, "else");
                SkipStatementSeparators(tokens, ref position);
                break;
            }

            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' in if statement.");
        }

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after if statement.");
        }

        return new IfStatementAst(clauses, elseBlock);
    }

    private static ForEachStatementAst ParseForEachStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.LParen)
        {
            throw new InvalidOperationException("Expected '(' after foreach.");
        }

        position++;
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.Variable)
        {
            throw new InvalidOperationException("Expected a loop variable after foreach '('.");
        }

        var variableName = tokens[position++].Text;
        if (!IsKeyword(tokens, position, "in"))
        {
            throw new InvalidOperationException("Expected 'in' after foreach loop variable.");
        }

        position++;
        var collection = ReadExpressionUntilRightParen(tokens, ref position, "foreach");
        var body = ReadScriptBlock(tokens, ref position, "foreach");
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after foreach statement.");
        }

        return new ForEachStatementAst(variableName, collection, body);
    }

    private static WhileStatementAst ParseWhileStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var condition = ReadParenthesizedExpression(tokens, ref position, "while");
        var body = ReadScriptBlock(tokens, ref position, "while");
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after while statement.");
        }

        return new WhileStatementAst(condition, body);
    }

    private static DoWhileStatementAst ParseDoWhileStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var body = ReadScriptBlock(tokens, ref position, "do");
        SkipStatementSeparators(tokens, ref position);

        var until = false;
        if (IsKeyword(tokens, position, "while"))
        {
            position++;
        }
        else if (IsKeyword(tokens, position, "until"))
        {
            position++;
            until = true;
        }
        else
        {
            throw new InvalidOperationException("Expected while or until after do block.");
        }

        var condition = ReadParenthesizedExpression(tokens, ref position, until ? "until" : "while");
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after do statement.");
        }

        return new DoWhileStatementAst(body, condition, until);
    }

    private static ForStatementAst ParseForStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var sections = ReadForSections(tokens, ref position);
        var body = ReadScriptBlock(tokens, ref position, "for");
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after for statement.");
        }

        return new ForStatementAst(
            sections[0].Count == 0 ? null : ParseStatement(sections[0]),
            sections[1].Count == 0 ? null : ParseParenthesizedExpressionValue(sections[1]),
            sections[2].Count == 0 ? null : ParseStatement(sections[2]),
            body);
    }

    private static SwitchStatementAst ParseSwitchStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var (matchMode, caseSensitive) = ReadSwitchOptions(tokens, ref position);
        var input = ReadParenthesizedExpression(tokens, ref position, "switch");
        var (clauses, defaultBlocks) = ReadSwitchClauses(tokens, ref position);
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after switch statement.");
        }

        return new SwitchStatementAst(input, clauses, defaultBlocks, matchMode, caseSensitive);
    }

    private static (SwitchMatchMode MatchMode, bool CaseSensitive) ReadSwitchOptions(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position)
    {
        // Native switch uses exact string matching unless a wildcard or regex option selects another mode.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L4578-L4603
        var matchMode = SwitchMatchMode.Exact;
        var caseSensitive = false;

        while (position < tokens.Count && tokens[position].Kind == PowerShellWasmTokenKind.Parameter)
        {
            var option = tokens[position++].Text;
            if (option.Equals("Regex", StringComparison.OrdinalIgnoreCase))
            {
                matchMode = SwitchMatchMode.Regex;
                continue;
            }

            if (option.Equals("CaseSensitive", StringComparison.OrdinalIgnoreCase))
            {
                caseSensitive = true;
                continue;
            }

            if (option.Equals("Wildcard", StringComparison.OrdinalIgnoreCase))
            {
                matchMode = SwitchMatchMode.Wildcard;
                continue;
            }

            if (option.Equals("Exact", StringComparison.OrdinalIgnoreCase))
            {
                matchMode = SwitchMatchMode.Exact;
                continue;
            }

            throw new InvalidOperationException($"Unsupported browser-safe switch option '-{option}'.");
        }

        return (matchMode, caseSensitive);
    }

    private static (IReadOnlyList<SwitchClauseAst> Clauses, IReadOnlyList<ScriptAst> DefaultBlocks) ReadSwitchClauses(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position)
    {
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.LBrace)
        {
            throw new InvalidOperationException("Expected '{' after switch.");
        }

        position++;
        var clauses = new List<SwitchClauseAst>();
        var defaultBlocks = new List<ScriptAst>();

        while (position < tokens.Count)
        {
            SkipStatementSeparators(tokens, ref position);
            if (position < tokens.Count && tokens[position].Kind == PowerShellWasmTokenKind.RBrace)
            {
                position++;
                return (clauses, defaultBlocks);
            }

            if (IsKeyword(tokens, position, "default"))
            {
                position++;
                defaultBlocks.Add(ReadScriptBlock(tokens, ref position, "switch default"));
                continue;
            }

            var patternTokens = ReadSwitchPattern(tokens, ref position);
            clauses.Add(new SwitchClauseAst(ParseExpression(patternTokens), ReadScriptBlock(tokens, ref position, "switch clause")));
        }

        throw new InvalidOperationException("Expected '}' to close switch.");
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadSwitchPattern(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.LBrace && depth == 0)
            {
                var pattern = tokens.Skip(start).Take(position - start).ToArray();
                if (pattern.Length == 0)
                {
                    throw new InvalidOperationException("Expected a switch pattern before '{'.");
                }

                return pattern;
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException("Expected switch clause body.");
    }

    private static IReadOnlyList<IReadOnlyList<PowerShellWasmToken>> ReadForSections(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position)
    {
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.LParen)
        {
            throw new InvalidOperationException("Expected '(' after for.");
        }

        position++;
        var sections = new List<IReadOnlyList<PowerShellWasmToken>>();
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RParen && depth == 0)
            {
                sections.Add(tokens.Skip(start).Take(position - start).ToArray());
                position++;
                if (sections.Count != 3)
                {
                    throw new InvalidOperationException("A for statement requires initializer, condition, and iterator sections.");
                }

                return sections;
            }

            if (tokens[position].Kind == PowerShellWasmTokenKind.Semicolon && depth == 0)
            {
                sections.Add(tokens.Skip(start).Take(position - start).ToArray());
                position++;
                start = position;
                continue;
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException("Expected ')' to close for statement.");
    }

    private static FunctionDefinitionStatementAst ParseFunctionDefinition(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.Identifier)
        {
            throw new InvalidOperationException("Expected a function name after function.");
        }

        var name = tokens[position++].Text;
        var parameters = CurrentKind(tokens, position) == PowerShellWasmTokenKind.LParen
            ? ReadFunctionParameters(tokens, ref position)
            : [];
        // Native permits an empty signature plus a body param block, but a nonempty signature conflicts even with param().
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/SemanticChecks.cs#L434-L445
        var body = ReadScriptBlock(tokens, ref position, "function", allowParamBlock: parameters.Count == 0);

        if (parameters.Count == 0 && body.Parameters.Count > 0)
        {
            parameters = body.Parameters;
            body = new ScriptAst(body.Statements);
        }

        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after function definition.");
        }

        return new FunctionDefinitionStatementAst(name, parameters, body);
    }

    private static ParamBlockStatementAst ParseParamBlock(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var parameters = ReadFunctionParameters(tokens, ref position);
        SkipStatementSeparators(tokens, ref position);

        if (position < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' after param block.");
        }

        return new ParamBlockStatementAst(parameters);
    }

    private static bool TryParseMetadataAttributeStatement(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out MetadataAttributeStatementAst statement)
    {
        statement = null!;
        if (!TryReadBracketAnnotation(tokens, 0, out var annotation, out var afterPosition) ||
            !IsScriptMetadataAttributeAnnotation(annotation))
        {
            return false;
        }

        var position = afterPosition;
        SkipStatementSeparators(tokens, ref position);
        if (position < tokens.Count)
        {
            return false;
        }

        statement = new MetadataAttributeStatementAst(GetAttributeAnnotationName(annotation));
        return true;
    }

    // Behavioral guidance: ParameterListRule / ParameterRule require comma-separated declarations and permit newlines within them.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L914-L1051
    private static IReadOnlyList<ParameterDeclarationAst> ReadFunctionParameters(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var parameters = new List<ParameterDeclarationAst>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CurrentKind(tokens, position) != PowerShellWasmTokenKind.LParen)
        {
            throw new InvalidOperationException("Expected '(' before function parameter list.");
        }
        position++;
        SkipParameterNewLines(tokens, ref position);

        if (CurrentKind(tokens, position) != PowerShellWasmTokenKind.RParen)
        {
            while (true)
            {
                var parameter = ReadParameterDeclaration(tokens, ref position);
                // Duplicate formal names compare the original variable paths without case sensitivity.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/SemanticChecks.cs#L85-L118
                if (!names.Add(parameter.Name))
                {
                    throw new InvalidOperationException($"Duplicate parameter '${parameter.Name}' in parameter list.");
                }
                parameters.Add(parameter);
                SkipParameterNewLines(tokens, ref position);
                if (CurrentKind(tokens, position) == PowerShellWasmTokenKind.RParen) break;
                if (CurrentKind(tokens, position) != PowerShellWasmTokenKind.Comma)
                {
                    throw new InvalidOperationException("Expected ',' between parameter declarations or ')' to close parameter list.");
                }
                position++;
                SkipParameterNewLines(tokens, ref position);
            }
        }

        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.RParen)
        {
            throw new InvalidOperationException("Expected ')' to close function parameter list.");
        }

        position++;
        return parameters;
    }

    private static void SkipParameterNewLines(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        while (CurrentKind(tokens, position) == PowerShellWasmTokenKind.NewLine) position++;
    }

    private static ParameterDeclarationAst ReadParameterDeclaration(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var annotations = new List<string>();
        while (CurrentKind(tokens, position) == PowerShellWasmTokenKind.LBracket)
        {
            annotations.Add(ReadParameterTypeName(tokens, ref position));
            SkipParameterNewLines(tokens, ref position);
        }

        // ParameterAst.StaticType uses the first type constraint; attributes never replace it.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/ast.cs#L2383-L2399
        var typeConstraints = annotations.Where(static annotation => !IsParameterAttributeAnnotation(annotation)).ToArray();
        var typeName = typeConstraints.FirstOrDefault();

        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.Variable)
        {
            throw new InvalidOperationException("Function parameter lists support optional attributes and type literals followed by variable names.");
        }

        var name = tokens[position++].Text;
        SkipParameterNewLines(tokens, ref position);
        ExpressionAst? defaultValue = null;
        if (CurrentKind(tokens, position) == PowerShellWasmTokenKind.Equals)
        {
            position++;
            var defaultTokens = ReadParameterDefaultValue(tokens, ref position);
            if (defaultTokens.Count == 0)
            {
                throw new InvalidOperationException($"Expected a default value for parameter '${name}'.");
            }

            defaultValue = ParseExpression(defaultTokens);
        }

        return new ParameterDeclarationAst(name, typeName, defaultValue, ExtractParameterAliases(annotations), ExtractValidateSetValues(annotations))
            { AdditionalTypeNames = typeConstraints.Skip(1).ToArray() };
    }

    private static IReadOnlyList<string> ExtractParameterAliases(IEnumerable<string> annotations)
    {
        var aliases = new List<string>();
        foreach (var annotation in annotations)
        {
            if (!GetAttributeAnnotationName(annotation).Equals("Alias", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var alias in ExtractAttributeStringArguments(annotation))
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    aliases.Add(alias);
                }
            }
        }

        return aliases;
    }

    private static IReadOnlyList<string> ExtractValidateSetValues(IEnumerable<string> annotations)
    {
        var values = new List<string>();
        foreach (var annotation in annotations)
        {
            if (!GetAttributeAnnotationName(annotation).Equals("ValidateSet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values.AddRange(ExtractAttributeStringArguments(annotation));
        }

        return values;
    }

    private static IReadOnlyList<string> ExtractAttributeStringArguments(string annotation)
    {
        var openParen = annotation.IndexOf('(', StringComparison.Ordinal);
        var closeParen = annotation.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
        {
            return [];
        }

        return annotation[(openParen + 1)..closeParen]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.Trim('\'', '"'))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool IsParameterAttributeAnnotation(string annotation)
    {
        var name = GetAttributeAnnotationName(annotation);
        return name.Equals("Parameter", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Alias", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AllowNull", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AllowEmptyString", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AllowEmptyCollection", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Validate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScriptMetadataAttributeAnnotation(string annotation)
    {
        var name = GetAttributeAnnotationName(annotation);
        return name.Equals("CmdletBinding", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OutputType", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SuppressMessage", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Diagnostics.CodeAnalysis.SuppressMessage", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System.Diagnostics.CodeAnalysis.SuppressMessage", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAttributeAnnotationName(string annotation)
    {
        var name = annotation;
        var parenIndex = name.IndexOf('(', StringComparison.Ordinal);
        if (parenIndex >= 0)
        {
            name = name[..parenIndex];
        }

        if (name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"Attribute".Length];
        }

        const string fullNamespace = "System.Management.Automation.";
        const string implicitSystemNamespace = "Management.Automation.";
        if (name.StartsWith(fullNamespace, StringComparison.OrdinalIgnoreCase)) name = name[fullNamespace.Length..];
        else if (name.StartsWith(implicitSystemNamespace, StringComparison.OrdinalIgnoreCase)) name = name[implicitSystemNamespace.Length..];

        return name;
    }

    private static string ReadParameterTypeName(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        position++;
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RBracket && depth == 0)
            {
                var typeName = string.Concat(tokens.Skip(start).Take(position - start).Select(static token => token.Text));
                position++;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    throw new InvalidOperationException("Expected a type name in parameter type literal.");
                }

                return typeName;
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException("Expected ']' to close parameter type literal.");
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadParameterDefaultValue(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position)
    {
        SkipParameterNewLines(tokens, ref position);
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (depth == 0 && tokens[position].Kind is PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.RParen)
            {
                break;
            }

            if (depth == 0 && tokens[position].Kind == PowerShellWasmTokenKind.NewLine &&
                !IsParameterDefaultContinuation(tokens, start, position))
            {
                break;
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        return RemoveTopLevelNewLines(tokens.Skip(start).Take(position - start).ToArray());
    }

    private static bool IsParameterDefaultContinuation(IReadOnlyList<PowerShellWasmToken> tokens, int start, int position)
    {
        while (position > start && tokens[position - 1].Kind == PowerShellWasmTokenKind.NewLine) position--;
        if (position == start) return true;
        var previousToken = tokens[--position];
        var previous = previousToken.Kind;
        if (previous == PowerShellWasmTokenKind.QuestionDot ||
            previous == PowerShellWasmTokenKind.Identifier && previousToken.Text.EndsWith(".", StringComparison.Ordinal)) return true;
        if (previous.IsUnaryOperator() || !CanEndStatementBeforeNewLine(previous)) return true;
        if (previous is not (PowerShellWasmTokenKind.PlusPlus or PowerShellWasmTokenKind.MinusMinus)) return false;
        var operandBefore = LastSignificantTokenKind(tokens, start, position);
        return operandBefore is null || operandBefore.Value.IsUnaryOperator() || !CanEndStatementBeforeNewLine(operandBefore.Value);
    }

    private static ExpressionAst ReadParenthesizedExpression(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position,
        string statementName)
    {
        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.LParen)
        {
            throw new InvalidOperationException($"Expected '(' after {statementName}.");
        }

        position++;
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RParen && depth == 0)
            {
                var expressionTokens = tokens.Skip(start).Take(position - start).ToArray();
                position++;
                return ParseParenthesizedExpressionValue(expressionTokens);
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException($"Expected ')' to close {statementName} condition.");
    }

    private static ExpressionAst ReadExpressionUntilRightParen(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position,
        string statementName)
    {
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RParen && depth == 0)
            {
                var expressionTokens = tokens.Skip(start).Take(position - start).ToArray();
                position++;
                return ParseParenthesizedExpressionValue(expressionTokens);
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        throw new InvalidOperationException($"Expected ')' to close {statementName} expression.");
    }

    private static TryStatementAst ParseTryStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var position = 1;
        var tryBlock = ReadScriptBlock(tokens, ref position, "try");
        var catches = new List<CatchClauseAst>();
        ScriptAst? finallyBlock = null;

        while (position < tokens.Count)
        {
            if (IsCatchClauseStart(tokens, position))
            {
                position++;
                var types = new List<string>();
                if (CurrentKind(tokens, position) == PowerShellWasmTokenKind.LBracket)
                {
                    do
                    {
                        if (!TryReadCatchType(tokens, ref position, out var typeName))
                            throw new InvalidOperationException("Expected a type literal in the catch clause.");
                        types.Add(typeName);
                        if (CurrentKind(tokens, position) != PowerShellWasmTokenKind.Comma) break;
                        position++;
                    } while (true);
                }

                ValidateCatchTypes(catches, types);
                catches.Add(new CatchClauseAst(types, ReadScriptBlock(tokens, ref position, "catch", isCatchBody: true)));
                continue;
            }

            if (IsKeyword(tokens, position, "finally"))
            {
                position++;
                finallyBlock = ReadScriptBlock(tokens, ref position, "finally", isFinallyBody: true);
                break;
            }

            throw new InvalidOperationException($"Unexpected token '{tokens[position].Text}' in try statement.");
        }

        if (catches.Count == 0 && finallyBlock is null)
        {
            throw new InvalidOperationException("A try statement requires at least one catch or finally block.");
        }

        return new TryStatementAst(tryBlock, catches.Select(clause => clause.Body).ToArray(), finallyBlock)
            { CatchTypeNames = catches.Select(clause => clause.TypeNames).ToArray() };
    }

    // PowerShell's catch grammar requires comma-separated type literals, not arbitrary header expressions.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L5494-L5669
    private static bool TryReadCatchType(IReadOnlyList<PowerShellWasmToken> tokens, ref int position, out string typeName)
    {
        typeName = string.Empty;
        if (!TryReadBracketAnnotation(tokens, position, out _, out var after)) return false;
        var content = tokens.Skip(position + 1).Take(after - position - 2)
            .SkipWhile(token => token.Kind == PowerShellWasmTokenKind.NewLine).ToArray();
        if (!IsCatchTypeSyntax(content)) return false;
        typeName = string.Concat(content.Select(token => token.Text));
        position = after;
        return true;
    }

    private static bool IsCatchTypeSyntax(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var cursor = 0;
        bool ReadType(bool allowAssembly)
        {
            while (CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.NewLine) cursor++;
            if (CurrentKind(tokens, cursor) != PowerShellWasmTokenKind.Identifier ||
                tokens[cursor].Text.Length == 0 || !(char.IsLetter(tokens[cursor].Text[0]) || tokens[cursor].Text[0] == '_'))
                return false;
            var start = cursor;
            while (cursor < tokens.Count && tokens[cursor].Kind is PowerShellWasmTokenKind.Identifier or
                PowerShellWasmTokenKind.Number or PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.DotDot)
            {
                if (cursor > start && tokens[cursor].HasLeadingWhitespace) break;
                if (tokens[cursor].Text.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '+' or '`'))) return false;
                cursor++;
            }

            var hasSuffix = false;
            while (CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.LBracket)
            {
                cursor++;
                while (CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.NewLine) cursor++;
                if (CurrentKind(tokens, cursor) is PowerShellWasmTokenKind.RBracket or PowerShellWasmTokenKind.Comma)
                {
                    while (CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.Comma) cursor++;
                }
                else
                {
                    if (hasSuffix) return false;
                    if (!ReadType(allowAssembly: false)) return false;
                    while (CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.Comma)
                    {
                        cursor++;
                        if (!ReadType(allowAssembly: false)) return false;
                    }
                }
                if (CurrentKind(tokens, cursor) != PowerShellWasmTokenKind.RBracket) return false;
                cursor++;
                hasSuffix = true;
            }

            if (allowAssembly && CurrentKind(tokens, cursor) == PowerShellWasmTokenKind.Comma)
            {
                // Assembly-qualified names are syntactically legal but still restricted by the runtime allowlist.
                cursor++;
                if (CurrentKind(tokens, cursor) is not (PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Number or
                    PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus or PowerShellWasmTokenKind.Comma)) return false;
                var assemblyStart = cursor;
                while (cursor < tokens.Count)
                {
                    if (tokens[cursor].Kind is not (PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Number or
                        PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.Equals or PowerShellWasmTokenKind.Minus or
                        PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.DotDot)) return false;
                    if (cursor > assemblyStart && tokens[cursor].HasLeadingWhitespace &&
                        tokens[cursor - 1].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Number &&
                        tokens[cursor].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Number) return false;
                    cursor++;
                }
            }
            return true;
        }
        return ReadType(allowAssembly: true) && cursor == tokens.Count;
    }

    // Semantic checks compare different clauses; repeated or overlapping filters within one clause are valid.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/SemanticChecks.cs#L529-L582
    private static void ValidateCatchTypes(IReadOnlyList<CatchClauseAst> previous, IReadOnlyList<string> types)
    {
        foreach (var clause in previous)
        {
            if (clause.TypeNames.Count == 0)
                throw new InvalidOperationException("A catch-all clause must be the last catch clause.");
            foreach (var earlier in clause.TypeNames)
            {
                if (!PowerShellWasmExceptionTypes.TryResolve(earlier, out var baseType)) continue;
                foreach (var current in types)
                    if (PowerShellWasmExceptionTypes.TryResolve(current, out var derivedType) &&
                        PowerShellWasmExceptionTypes.IsAssignableFrom(baseType, derivedType))
                        throw new InvalidOperationException($"Exception type [{current}] is already handled by a previous catch block.");
            }
        }
    }

    private static ScriptAst ReadScriptBlock(
        IReadOnlyList<PowerShellWasmToken> tokens, ref int position, string blockName, bool isCatchBody = false,
        bool isFinallyBody = false, bool allowParamBlock = true)
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
                IReadOnlyList<PowerShellWasmToken> bodyTokens = tokens.Skip(start).Take(position - start).ToArray();
                position++;
                if (isCatchBody) bodyTokens = MarkInsideCatch(bodyTokens);
                if (isFinallyBody) bodyTokens = MarkInsideFinally(bodyTokens, true);
                // Signature parameter ASTs are outside the function body; body param blocks are inside this boundary.
                if (blockName == "function") bodyTokens = MarkInsideFinally(bodyTokens, false);
                return ParseScript(bodyTokens, allowParamBlock);
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
        if (tokens.Count == 0 || !IsCommandNameToken(tokens[0]))
        {
            throw new InvalidOperationException("Expected a command name.");
        }

        // Argument scanning may split an embedded @(...)/@{...} into a literal @ and a new expression.
        tokens = tokens.ToList();
        var parameters = new List<CommandParameterAst>();
        var arguments = new List<CommandArgumentAst>();
        var position = 1;
        var commandName = GetCommandName(tokens[0]);
        while (position < tokens.Count && Contiguous(tokens[position - 1], tokens[position]) &&
            tokens[position].Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Parameter or
                PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral or
                PowerShellWasmTokenKind.Variable or PowerShellWasmTokenKind.Number or PowerShellWasmTokenKind.DollarLParen)
        {
            var fragment = tokens[position++];
            if (fragment.Kind == PowerShellWasmTokenKind.DollarLParen)
            {
                var start = --position;
                ReadArgumentGroup(tokens, ref position);
                var end = tokens[position - 1].Offset + tokens[position - 1].Length;
                commandName += fragment.SourceScript?[fragment.Offset..end] ??
                    string.Concat(tokens.Skip(start).Take(position - start).Select(GetBareCommandTokenText));
                continue;
            }
            commandName += fragment.Kind == PowerShellWasmTokenKind.Variable
                ? fragment.SourceScript?[fragment.Offset..(fragment.Offset + fragment.Length)] ?? "$" + fragment.Text
                : GetBareCommandTokenText(fragment);
        }

        while (position < tokens.Count)
        {
            var sourceOrder = position;
            if (tokens[position].Kind == PowerShellWasmTokenKind.Parameter && tokens[position].HasLeadingWhitespace &&
                !HasQuotedSuffix(tokens, position))
            {
                var name = tokens[position++].Text;
                var inline = position < tokens.Count && tokens[position].Kind == PowerShellWasmTokenKind.Colon;
                if (inline) position++;
                if (position >= tokens.Count || tokens[position].Kind == PowerShellWasmTokenKind.Parameter
                    || IsSplatStart(tokens, position) || IsOperatorParameter(tokens, position))
                {
                    if (inline) throw new InvalidOperationException($"Expected a value after '-{name}:'.");
                    parameters.Add(new(name, null) { SourceOrder = sourceOrder });
                    continue;
                }

                var argumentTokens = ReadCommandArgument(tokens, ref position);
                parameters.Add(new(name, ParseParameterArgumentExpression(name, argumentTokens))
                    { SourceOrder = sourceOrder, IsInlineValue = inline });
                continue;
            }

            if (TryReadOperatorParameter(tokens, ref position, out var operatorParameter))
            {
                parameters.Add(operatorParameter with { SourceOrder = sourceOrder });
                continue;
            }

            if (IsSplatStart(tokens, position))
            {
                var name = tokens[position + 1].Text;
                arguments.Add(new CommandArgumentAst(new VariableExpressionAst(name, false), IsSplat: true)
                    { SourceOrder = sourceOrder });
                position += 2;
                continue;
            }

            arguments.Add(new CommandArgumentAst(ParseCommandArgumentExpression(ReadCommandArgument(tokens, ref position)))
                { SourceOrder = sourceOrder });
        }

        return new CommandAst(commandName, parameters, arguments);
    }

    private static bool TryReadOperatorParameter(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position,
        out CommandParameterAst parameter)
    {
        parameter = null!;
        if (HasQuotedSuffix(tokens, position) || !TryGetOperatorParameterName(tokens[position].Kind, out var parameterName))
        {
            return false;
        }

        position++;
        if (position >= tokens.Count ||
            tokens[position].Kind == PowerShellWasmTokenKind.Parameter ||
            IsSplatStart(tokens, position) ||
            IsOperatorParameter(tokens, position))
        {
            parameter = new(parameterName, null);
            return true;
        }

        var argumentTokens = ReadCommandArgument(tokens, ref position);
        parameter = new(parameterName, ParseParameterArgumentExpression(parameterName, argumentTokens));
        return true;
    }

    private static bool IsOperatorParameter(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        position < tokens.Count && !HasQuotedSuffix(tokens, position) && TryGetOperatorParameterName(tokens[position].Kind, out _);

    private static bool HasQuotedSuffix(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        position + 1 < tokens.Count && Contiguous(tokens[position], tokens[position + 1]) &&
        tokens[position + 1].Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral;

    private static bool TryGetOperatorParameterName(PowerShellWasmTokenKind kind, out string name)
    {
        name = kind switch
        {
            PowerShellWasmTokenKind.Ieq => "EQ",
            PowerShellWasmTokenKind.Ine => "NE",
            PowerShellWasmTokenKind.Ige => "GE",
            PowerShellWasmTokenKind.Igt => "GT",
            PowerShellWasmTokenKind.Ilt => "LT",
            PowerShellWasmTokenKind.Ile => "LE",
            PowerShellWasmTokenKind.Ilike => "Like",
            PowerShellWasmTokenKind.Inotlike => "NotLike",
            PowerShellWasmTokenKind.Imatch => "Match",
            PowerShellWasmTokenKind.Inotmatch => "NotMatch",
            PowerShellWasmTokenKind.Icontains => "Contains",
            PowerShellWasmTokenKind.Inotcontains => "NotContains",
            PowerShellWasmTokenKind.Iin => "In",
            PowerShellWasmTokenKind.Inotin => "NotIn",
            PowerShellWasmTokenKind.Ceq => "CEQ",
            PowerShellWasmTokenKind.Cne => "CNE",
            PowerShellWasmTokenKind.Cge => "CGE",
            PowerShellWasmTokenKind.Cgt => "CGT",
            PowerShellWasmTokenKind.Clt => "CLT",
            PowerShellWasmTokenKind.Cle => "CLE",
            PowerShellWasmTokenKind.Clike => "CLike",
            PowerShellWasmTokenKind.Cnotlike => "CNotLike",
            PowerShellWasmTokenKind.Cmatch => "CMatch",
            PowerShellWasmTokenKind.Cnotmatch => "CNotMatch",
            PowerShellWasmTokenKind.Ccontains => "CContains",
            PowerShellWasmTokenKind.Cnotcontains => "CNotContains",
            PowerShellWasmTokenKind.Cin => "CIn",
            PowerShellWasmTokenKind.Cnotin => "CNotIn",
            _ => string.Empty
        };

        return name.Length > 0;
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadCommandArgument(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var result = new List<PowerShellWasmToken>(ReadSingleCommandArgument(tokens, ref position));
        while (position < tokens.Count && tokens[position].Kind == PowerShellWasmTokenKind.Comma)
        {
            result.Add(tokens[position++]);
            if (position >= tokens.Count || tokens[position].Kind == PowerShellWasmTokenKind.Parameter)
            {
                throw new InvalidOperationException("Expected a command argument after ','.");
            }

            result.AddRange(ReadSingleCommandArgument(tokens, ref position));
        }

        return result;
    }

    private static IReadOnlyList<PowerShellWasmToken> ReadSingleCommandArgument(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var start = position;
        if (tokens[position].Kind == PowerShellWasmTokenKind.Comma)
            throw new InvalidOperationException("Expected a command argument before ','.");
        if (tokens[position].Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral)
        {
            position++;
            ReadCommandArgumentPostfix(tokens, ref position, allowConditional: true);
            return tokens.Skip(start).Take(position - start).ToArray();
        }
        if (tokens[position].Kind is PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or
            PowerShellWasmTokenKind.AtLBrace or PowerShellWasmTokenKind.AtLParen or PowerShellWasmTokenKind.DollarLParen)
        {
            ReadArgumentGroup(tokens, ref position);
            ReadCommandArgumentPostfix(tokens, ref position, allowConditional: true);
            return tokens.Skip(start).Take(position - start).ToArray();
        }
        var variable = tokens[position++].Kind == PowerShellWasmTokenKind.Variable;
        if (variable && ReadCommandArgumentPostfix(tokens, ref position, allowConditional: false))
            return tokens.Skip(start).Take(position - start).ToArray();
        var conditionalText = variable && position < tokens.Count &&
            (tokens[position].Kind == PowerShellWasmTokenKind.QuestionDot || IsNullConditionalIndexOperator(tokens, position));
        while (position < tokens.Count && (Contiguous(tokens[position - 1], tokens[position]) ||
            conditionalText && HasMemberAccessAdjacency(tokens[position - 1], tokens[position])))
        {
            var kind = tokens[position].Kind;
            if (kind is PowerShellWasmTokenKind.AtLParen or PowerShellWasmTokenKind.AtLBrace &&
                tokens is List<PowerShellWasmToken> mutable)
            {
                var original = tokens[position];
                mutable[position] = original with { Kind = kind == PowerShellWasmTokenKind.AtLParen
                    ? PowerShellWasmTokenKind.LParen : PowerShellWasmTokenKind.LBrace,
                    Text = kind == PowerShellWasmTokenKind.AtLParen ? "(" : "{", Offset = original.Offset + 1, Length = 1 };
                return tokens.Skip(start).Take(position - start).Append(original with
                    { Kind = PowerShellWasmTokenKind.Identifier, Text = "@", Length = 1 }).ToArray();
            }
            if (kind is PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or
                PowerShellWasmTokenKind.RParen or PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.Semicolon or
                PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.EndOfInput) break;
            if (kind == PowerShellWasmTokenKind.DollarLParen) ReadArgumentGroup(tokens, ref position);
            else position++;
        }
        return tokens.Skip(start).Take(position - start).ToArray();
    }

    // Native command generic tokens retain literal text; primary expression targets apply member/index operators.
    // Initial variable arguments containing ? are generic text, while an ordinary member/index chain is an expression.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L6300-L6392
    private static bool ReadCommandArgumentPostfix(IReadOnlyList<PowerShellWasmToken> tokens, ref int position, bool allowConditional)
    {
        var hasMember = false;
        while (position < tokens.Count && (Contiguous(tokens[position - 1], tokens[position]) ||
            (allowConditional || hasMember) && HasMemberAccessAdjacency(tokens[position - 1], tokens[position])))
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.LBracket)
            {
                ReadArgumentGroup(tokens, ref position);
                hasMember = true;
            }
            else if ((allowConditional || hasMember) && IsNullConditionalIndexOperator(tokens, position))
            {
                position++;
                ReadArgumentGroup(tokens, ref position);
                hasMember = true;
            }
            else if ((allowConditional || hasMember) && tokens[position].Kind == PowerShellWasmTokenKind.QuestionDot ||
                tokens[position].Kind == PowerShellWasmTokenKind.Identifier && tokens[position].Text.StartsWith('.'))
            {
                // In argument mode a dot before whitespace/end is a separate literal argument, not member access.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L4167-L4208
                if (tokens[position].Kind == PowerShellWasmTokenKind.Identifier && tokens[position].Text == "." &&
                    (position + 1 == tokens.Count || tokens[position + 1].HasLeadingWhitespace))
                    return true;
                var computed = tokens[position].Kind == PowerShellWasmTokenKind.QuestionDot || tokens[position].Text == ".";
                position++;
                if (computed && position < tokens.Count)
                {
                    if (tokens[position].Kind == PowerShellWasmTokenKind.LParen) ReadArgumentGroup(tokens, ref position);
                    else position++;
                }
                hasMember = true;
                if (position < tokens.Count && tokens[position].Kind == PowerShellWasmTokenKind.LParen &&
                    Contiguous(tokens[position - 1], tokens[position])) ReadArgumentGroup(tokens, ref position);
            }
            else break;
        }
        return hasMember;
    }

    private static bool IsNullConditionalIndexOperator(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        CurrentKind(tokens, position) == PowerShellWasmTokenKind.Question &&
        CurrentKind(tokens, position + 1) == PowerShellWasmTokenKind.LBracket &&
        Contiguous(tokens[position], tokens[position + 1]);

    private static bool Contiguous(PowerShellWasmToken left, PowerShellWasmToken right) =>
        !right.HasLeadingWhitespace && right.Offset == left.Offset + left.Length;

    // Native member scanning skips adjacent block comments, but no whitespace outside those comments.
    // Keep this distinct from generic command concatenation, where a comment-shaped fragment may be literal text.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L4167-L4190
    private static bool HasMemberAccessAdjacency(PowerShellWasmToken left, PowerShellWasmToken right)
    {
        if (Contiguous(left, right)) return true;
        if (left.SourceScript is not { } source || right.SourceScript != source) return false;
        var position = left.Offset + left.Length;
        if (position >= right.Offset) return false;
        while (position < right.Offset)
        {
            if (position + 1 >= right.Offset || source[position] != '<' || source[position + 1] != '#') return false;
            var close = source.IndexOf("#>", position + 2, StringComparison.Ordinal);
            if (close < 0 || close + 2 > right.Offset) return false;
            position = close + 2;
        }
        return true;
    }

    private static void ReadArgumentGroup(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var closing = tokens[position].Kind switch
        {
            PowerShellWasmTokenKind.LBrace or PowerShellWasmTokenKind.AtLBrace => PowerShellWasmTokenKind.RBrace,
            PowerShellWasmTokenKind.LBracket => PowerShellWasmTokenKind.RBracket,
            _ => PowerShellWasmTokenKind.RParen
        };
        var depth = 0;
        do { UpdateDepth(tokens[position++], ref depth); }
        while (position < tokens.Count && depth > 0);
        if (depth != 0) throw new InvalidOperationException($"Expected token {closing} but found EndOfInput.");
    }

    // Argument mode combines generic text with variables and quoted/subexpression fragments, but initial
    // quoted strings and expression forms end their argument. Arithmetic text is not an expression here.
    // https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_parsing#argument-mode
    private static ExpressionAst ParseCommandArgumentExpression(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var items = new List<ExpressionAst>();
        var position = 0;
        do
        {
            var part = ReadSingleCommandArgument(tokens, ref position);
            items.Add(ParseSingleCommandArgument(part));
            if (position == tokens.Count) break;
            if (tokens[position++].Kind != PowerShellWasmTokenKind.Comma)
                throw new InvalidOperationException("Unexpected token in command argument.");
        } while (position < tokens.Count);
        return items.Count == 1 ? items[0] : new ArrayExpressionAst(items);
    }

    private static ExpressionAst ParseSingleCommandArgument(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        // Argument mode retains an out-of-range numeric-looking argument as text; expression mode rejects it.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L3884-L3917
        if (tokens.Count == 1 && tokens[0].Kind == PowerShellWasmTokenKind.Number)
            return PowerShellWasmNumericLiteral.TryParse(tokens[0].Text, out var number)
                ? new NumberExpressionAst(number) : new StringExpressionAst(tokens[0].Text, false);
        if (tokens.Count == 1 && tokens[0].Kind is PowerShellWasmTokenKind.Variable or
            PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral)
            return ParseExpression(tokens);
        if (IsCommandArgumentExpressionStart(tokens))
            return ParseExpression(tokens);
        var parts = new List<ExpressionAst>();
        for (var position = 0; position < tokens.Count;)
        {
            var token = tokens[position++];
            if (position > 1 && !Contiguous(tokens[position - 2], token) &&
                HasMemberAccessAdjacency(tokens[position - 2], token))
            {
                var previousEnd = tokens[position - 2].Offset + tokens[position - 2].Length;
                parts.Add(new StringExpressionAst(token.SourceScript![previousEnd..token.Offset], false));
            }
            if (token.Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral)
                parts.Add(ParseStringToken(token));
            else if (token.Kind == PowerShellWasmTokenKind.Variable) parts.Add(ParseExpression([token]));
            else if (token.Kind == PowerShellWasmTokenKind.DollarLParen)
            {
                var start = --position;
                ReadArgumentGroup(tokens, ref position);
                parts.Add(ParseExpression(tokens.Skip(start).Take(position - start).ToArray()));
            }
            else parts.Add(new StringExpressionAst(GetBareCommandTokenText(token), false));
        }
        return parts.Count == 1 ? parts[0] : new CommandStringExpressionAst(parts);
    }

    private static bool IsCommandArgumentExpressionStart(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 0 && (tokens[0].Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral or
            PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or PowerShellWasmTokenKind.AtLParen or
            PowerShellWasmTokenKind.AtLBrace or PowerShellWasmTokenKind.DollarLParen ||
            tokens[0].Kind == PowerShellWasmTokenKind.Variable && tokens.Count > 1 &&
            (tokens[1].Kind == PowerShellWasmTokenKind.LBracket ||
                tokens[1].Kind == PowerShellWasmTokenKind.Identifier && tokens[1].Text.StartsWith('.')));

    private static ExpressionAst ParseParameterArgumentExpression(string parameterName, IReadOnlyList<PowerShellWasmToken> tokens) =>
        IsAppendVariableCommonParameter(parameterName, tokens)
            ? new StringExpressionAst("+" + tokens[1].Text, IsExpandable: false)
            : ParseCommandArgumentExpression(tokens);

    private static bool IsAppendVariableCommonParameter(string parameterName, IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count == 2 &&
        tokens[0].Kind == PowerShellWasmTokenKind.Plus &&
        tokens[1].Kind == PowerShellWasmTokenKind.Identifier &&
        tokens[1].Offset == tokens[0].Offset + tokens[0].Length &&
        (parameterName.Equals("OutVariable", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("ov", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("ErrorVariable", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("ev", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("InformationVariable", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("iv", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("WarningVariable", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Equals("wv", StringComparison.OrdinalIgnoreCase));

    private static string GetBareCommandTokenText(PowerShellWasmToken token) =>
        token.Kind == PowerShellWasmTokenKind.Parameter ? "-" + token.Text : token.Text;

    private static ExpressionAst ParseExpression(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        if (TryParseSettableCompoundAssignmentExpression(tokens, out var assignment))
        {
            return assignment;
        }

        var parser = new ExpressionParser(tokens);
        return parser.Parse(requireComplete: true);
    }

    private static bool TryParseSettableCompoundAssignmentExpression(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out SettableCompoundAssignmentExpressionAst expression)
    {
        expression = null!;
        if (!TryFindTopLevelCompoundAssignment(tokens, out var operatorPosition, out var op))
        {
            return false;
        }

        var target = ParseExpression(tokens.Take(operatorPosition).ToArray());
        if (!IsSettableAssignmentTarget(target))
        {
            return false;
        }

        expression = new SettableCompoundAssignmentExpressionAst(
            target,
            op,
            ParseExpression(tokens.Skip(operatorPosition + 2).ToArray()));
        return true;
    }

    private static ExpressionAst ParseParenthesizedExpressionValue(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        // Behavioral guidance: ParenthesizedExpressionRule accepts one pipeline-chain; only $()/@() accept statement lists.
        // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/parser/Parser.cs
        // Browser-safe adaptation: preserve that distinction using the compact PSWasm AST, without upstream error recovery.
        if (HasTopLevelStatementSeparator(tokens))
        {
            throw new InvalidOperationException("Expected ')' after the parenthesized pipeline. Use '$()' for a statement list.");
        }

        var normalized = RemoveTopLevelNewLines(tokens);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("Expected an expression after '('.");
        }

        // Grouped assignments emit their assigned value, including command or control-statement output on the right.
        if (TryFindTopLevelCompoundAssignment(normalized, out var operatorPosition, out var assignmentOperator))
        {
            var target = new ExpressionParser(normalized.Take(operatorPosition).ToArray()).Parse(requireComplete: true);
            if (IsSettableAssignmentTarget(target))
            {
                return new SettableCompoundAssignmentExpressionAst(target, assignmentOperator,
                    ParseAssignmentValue(normalized.Skip(operatorPosition + 2).ToArray()));
            }
        }

        var equals = FindAssignmentEquals(normalized);
        if (equals > 0)
        {
            if (TryReadAssignmentTargets(normalized, equals, out var names) && names.Count > 1)
                return new ParallelAssignmentExpressionAst(names, ParseAssignmentValue(normalized.Skip(equals + 1).ToArray()));
            var target = new ExpressionParser(normalized.Take(equals).ToArray()).Parse(requireComplete: true);
            if (IsSimpleAssignmentTarget(target))
            {
                return new SettableAssignmentExpressionAst(target, ParseAssignmentValue(normalized.Skip(equals + 1).ToArray()));
            }
        }

        var chain = SplitTopLevelPipelineChain(normalized);
        if (chain.Segments.Count > 1)
        {
            var first = ParsePipeline(chain.Segments[0]);
            var clauses = chain.Operators.Select((op, index) =>
                new PipelineChainClauseAst(op, ParsePipeline(chain.Segments[index + 1]))).ToArray();
            return new StatementExpressionAst(new PipelineChainStatementAst(first, clauses));
        }

        var statement = ParsePipeline(normalized);
        return statement is ExpressionStatementAst expression ? expression.Expression : new StatementExpressionAst(statement);

        static ExpressionAst ParseAssignmentValue(IReadOnlyList<PowerShellWasmToken> valueTokens) =>
            IsStatementAssignmentValue(valueTokens)
                ? new StatementExpressionAst(ParseStatement(valueTokens))
                : new ExpressionParser(valueTokens).Parse(requireComplete: true);

        static StatementAst ParsePipeline(IReadOnlyList<PowerShellWasmToken> pipelineTokens)
        {
            var segments = SplitTopLevel(pipelineTokens, PowerShellWasmTokenKind.Pipe);
            if (segments.Count > 1)
            {
                return new PipelineStatementAst(segments.Select(segment => IsCommandSegment(segment)
                    ? (PipelineElementAst)new CommandPipelineElementAst(ParseCommand(segment))
                    : new ExpressionPipelineElementAst(new ExpressionParser(segment).Parse(requireComplete: true))).ToArray());
            }

            // Pipeline grammar treats words such as 'if' as command names here, not as control-flow statements.
            return IsCommandSegment(pipelineTokens)
                ? new CommandStatementAst(ParseCommand(pipelineTokens))
                : new ExpressionStatementAst(new ExpressionParser(pipelineTokens).Parse(requireComplete: true));
        }
    }

    private static bool IsCommandSegment(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 0 && IsCommandNameToken(tokens[0]);

    private static bool IsCommandNameToken(PowerShellWasmToken token) =>
        token.Kind == PowerShellWasmTokenKind.Identifier ||
        token.Kind == PowerShellWasmTokenKind.Question ||
        token.Kind == PowerShellWasmTokenKind.Remainder;

    private static string GetCommandName(PowerShellWasmToken token) =>
        token.Kind == PowerShellWasmTokenKind.Remainder ? "%" : token.Text;

    private static bool IsStatementExpressionValue(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 0 &&
        (SplitTopLevelPipelineChain(tokens).Segments.Count > 1 ||
            SplitTopLevel(tokens, PowerShellWasmTokenKind.Pipe).Count > 1 ||
            IsCommandExpressionSegment(tokens));

    private static bool HasTopLevelStatementSeparator(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && token.Kind == PowerShellWasmTokenKind.Semicolon)
            {
                return true;
            }

            if (depth == 0 &&
                token.Kind == PowerShellWasmTokenKind.NewLine &&
                IsStatementBoundaryNewLine(tokens, i))
            {
                return true;
            }

            UpdateDepth(token, ref depth);
        }

        return false;
    }

    private static bool IsStatementBoundaryNewLine(IReadOnlyList<PowerShellWasmToken> tokens, int position, int start = 0)
    {
        var previous = PreviousSignificantToken(tokens, position);
        var next = NextSignificantToken(tokens, position + 1);
        var previousPosition = position - 1;
        while (previousPosition >= 0 && tokens[previousPosition].Kind == PowerShellWasmTokenKind.NewLine) previousPosition--;
        // Native post-primary member operators consume newlines for fluent expression syntax.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7319-L7336
        if (previous.HasValue &&
            (previous.Value.Kind == PowerShellWasmTokenKind.QuestionDot && previousPosition > 0 &&
                HasMemberAccessAdjacency(tokens[previousPosition - 1], previous.Value) ||
                previous.Value.Kind == PowerShellWasmTokenKind.Identifier && previous.Value.Text.EndsWith(".", StringComparison.Ordinal) &&
                    (previous.Value.Text.Length > 1 || !previous.Value.HasLeadingWhitespace)) &&
            IsExpressionMemberContinuation(tokens, start, position)) return false;
        return previous.HasValue &&
            next.HasValue &&
            CanEndStatementBeforeNewLine(previous.Value.Kind) &&
            CanStartStatementAfterNewLine(next.Value.Kind);
    }

    private static bool IsExpressionMemberContinuation(IReadOnlyList<PowerShellWasmToken> tokens, int start, int position)
    {
        if (IsKeyword(tokens, start, "return") || IsKeyword(tokens, start, "throw")) start++;
        var expressionStart = start;
        var depth = 0;
        for (var index = start; index < position; index++)
        {
            if (depth == 0 && (IsPipelineSeparator(tokens[index].Kind) ||
                tokens[index].Kind == PowerShellWasmTokenKind.Equals && !IsCommandNameToken(tokens[expressionStart])))
            {
                expressionStart = index + 1;
                while (expressionStart < position && tokens[expressionStart].Kind == PowerShellWasmTokenKind.NewLine) expressionStart++;
            }
            UpdateDepth(tokens[index], ref depth);
        }
        while (expressionStart < position && tokens[expressionStart].Kind == PowerShellWasmTokenKind.NewLine) expressionStart++;
        if (expressionStart < position && IsCommandNameToken(tokens[expressionStart]) &&
            PreviousSignificantToken(tokens, position)?.Kind == PowerShellWasmTokenKind.QuestionDot)
        {
            // A command's expression argument may use fluent ?. access; an initial variable's literal ?. may not.
            var commandTokens = RemoveTopLevelNewLines(tokens.Skip(expressionStart).Take(position - expressionStart).ToArray());
            var argumentPosition = 1;
            while (argumentPosition < commandTokens.Count)
            {
                if (commandTokens[argumentPosition].Kind == PowerShellWasmTokenKind.Comma)
                {
                    argumentPosition++;
                    continue;
                }
                if (commandTokens[argumentPosition].Kind == PowerShellWasmTokenKind.Parameter &&
                    commandTokens[argumentPosition].HasLeadingWhitespace)
                {
                    argumentPosition++;
                    if (CurrentKind(commandTokens, argumentPosition) == PowerShellWasmTokenKind.Colon) argumentPosition++;
                    continue;
                }
                var argument = ReadSingleCommandArgument(commandTokens, ref argumentPosition);
                if (argumentPosition == commandTokens.Count) return IsCommandArgumentExpressionStart(argument);
            }
        }
        // In argument mode a trailing dot is part of the argument and does not continue the command onto the next line.
        return expressionStart < position && !IsCommandNameToken(tokens[expressionStart]);
    }

    private static PowerShellWasmToken? PreviousSignificantToken(IReadOnlyList<PowerShellWasmToken> tokens, int position)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (tokens[i].Kind != PowerShellWasmTokenKind.NewLine)
            {
                return tokens[i];
            }
        }

        return null;
    }

    private static PowerShellWasmToken? NextSignificantToken(IReadOnlyList<PowerShellWasmToken> tokens, int position)
    {
        for (var i = position; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != PowerShellWasmTokenKind.NewLine)
            {
                return tokens[i];
            }
        }

        return null;
    }

    private static bool CanEndStatementBeforeNewLine(PowerShellWasmTokenKind kind) =>
        kind is not (PowerShellWasmTokenKind.Semicolon or
            PowerShellWasmTokenKind.Pipe or
            PowerShellWasmTokenKind.PipelineChainAnd or
            PowerShellWasmTokenKind.PipelineChainOr or
            PowerShellWasmTokenKind.Equals or
            PowerShellWasmTokenKind.Question or
            PowerShellWasmTokenKind.Colon or
            PowerShellWasmTokenKind.Comma or
            PowerShellWasmTokenKind.LParen or
            PowerShellWasmTokenKind.LBrace or
            PowerShellWasmTokenKind.LBracket or
            PowerShellWasmTokenKind.At or
            PowerShellWasmTokenKind.AtLBrace or
            PowerShellWasmTokenKind.AtLParen or
            PowerShellWasmTokenKind.DollarLParen or
            PowerShellWasmTokenKind.DoubleColon) &&
        !kind.IsBinaryOperator();

    private static bool CanStartStatementAfterNewLine(PowerShellWasmTokenKind kind) =>
        kind is not (PowerShellWasmTokenKind.EndOfInput or
            PowerShellWasmTokenKind.NewLine or
            PowerShellWasmTokenKind.Semicolon or
            PowerShellWasmTokenKind.RParen or
            PowerShellWasmTokenKind.RBrace or
            PowerShellWasmTokenKind.RBracket or
            PowerShellWasmTokenKind.Pipe or
            PowerShellWasmTokenKind.PipelineChainAnd or
            PowerShellWasmTokenKind.PipelineChainOr or
            PowerShellWasmTokenKind.Colon);

    private static bool IsCommandExpressionSegment(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        if (!IsCommandSegment(tokens))
        {
            return false;
        }

        if (tokens.Count == 1)
        {
            return tokens[0].Text.Contains('-', StringComparison.Ordinal);
        }

        var second = tokens[1];
        if (second.Kind.IsBinaryOperator() || second.Kind == PowerShellWasmTokenKind.Comma)
        {
            return false;
        }

        return second.HasLeadingWhitespace ||
            second.Kind is not PowerShellWasmTokenKind.Identifier ||
            !second.Text.StartsWith(".", StringComparison.Ordinal);
    }

    private static bool IsStatementAssignmentValue(IReadOnlyList<PowerShellWasmToken> tokens) =>
        IsCommandSegment(tokens) ||
        SplitTopLevelPipelineChain(tokens).Segments.Count > 1 ||
        SplitTopLevel(tokens, PowerShellWasmTokenKind.Pipe).Count > 1;

    private static bool TryParseCompoundAssignmentStatement(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out CompoundAssignmentStatementAst statement)
    {
        statement = null!;
        if (tokens.Count < 4 ||
            tokens[0].Kind != PowerShellWasmTokenKind.Variable ||
            !TryMapCompoundAssignmentOperator(tokens[1].Kind, out var op) ||
            tokens[2].Kind != PowerShellWasmTokenKind.Equals)
        {
            return false;
        }

        statement = new CompoundAssignmentStatementAst(tokens[0].Text, op, ParseExpression(tokens.Skip(3).ToArray()));
        return true;
    }

    private static bool TryParseSettableCompoundAssignmentStatement(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out SettableCompoundAssignmentStatementAst statement)
    {
        statement = null!;
        if (!TryFindTopLevelCompoundAssignment(tokens, out var operatorPosition, out var op))
        {
            return false;
        }

        var target = ParseExpression(tokens.Take(operatorPosition).ToArray());
        if (!IsSettableAssignmentTarget(target))
        {
            return false;
        }

        statement = new SettableCompoundAssignmentStatementAst(
            target,
            op,
            ParseExpression(tokens.Skip(operatorPosition + 2).ToArray()));
        return true;
    }

    private static bool TryReadAssignmentTargets(
        IReadOnlyList<PowerShellWasmToken> tokens,
        int equals,
        out IReadOnlyList<string> variableNames)
    {
        var names = new List<string>();
        var expectVariable = true;

        for (var i = 0; i < equals; i++)
        {
            if (expectVariable)
            {
                if (tokens[i].Kind != PowerShellWasmTokenKind.Variable)
                {
                    variableNames = [];
                    return false;
                }

                names.Add(tokens[i].Text);
                expectVariable = false;
                continue;
            }

            if (tokens[i].Kind != PowerShellWasmTokenKind.Comma)
            {
                variableNames = [];
                return false;
            }

            expectVariable = true;
        }

        variableNames = names;
        return names.Count > 0 && !expectVariable;
    }

    private static bool IsSettableAssignmentTarget(ExpressionAst target) => target switch
    {
        VariableExpressionAst or MemberAccessExpressionAst or ComputedMemberAccessExpressionAst { NullConditional: false } or
            IndexExpressionAst { NullConditional: false } => true,
        CastExpressionAst cast => IsSettableAssignmentTarget(cast.Operand),
        ParenthesizedExpressionAst parenthesized => IsSettableAssignmentTarget(parenthesized.Expression),
        _ => false
    };

    // Only simple assignment can distribute a value across multiple targets; += and ++ require one writable location.
    private static bool IsSimpleAssignmentTarget(ExpressionAst target) => target switch
    {
        ArrayExpressionAst array => array.Items.Count > 0 && array.Items.All(IsSimpleAssignmentTarget),
        CastExpressionAst cast => IsSimpleAssignmentTarget(cast.Operand),
        ParenthesizedExpressionAst parenthesized => IsSimpleAssignmentTarget(parenthesized.Expression),
        _ => IsSettableAssignmentTarget(target)
    };

    private static bool TryFindTopLevelCompoundAssignment(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out int operatorPosition,
        out PowerShellWasmBinaryOperator op)
    {
        var depth = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (depth == 0 && IsPipelineSeparator(tokens[i].Kind))
            {
                break;
            }

            if (depth == 0 &&
                tokens[i + 1].Kind == PowerShellWasmTokenKind.Equals &&
                TryMapCompoundAssignmentOperator(tokens[i].Kind, out op))
            {
                operatorPosition = i;
                return true;
            }

            UpdateDepth(tokens[i], ref depth);
        }

        operatorPosition = -1;
        op = default;
        return false;
    }

    private static bool TryMapCompoundAssignmentOperator(PowerShellWasmTokenKind kind, out PowerShellWasmBinaryOperator op)
    {
        op = kind switch
        {
            PowerShellWasmTokenKind.Plus => PowerShellWasmBinaryOperator.Add,
            PowerShellWasmTokenKind.Minus => PowerShellWasmBinaryOperator.Subtract,
            PowerShellWasmTokenKind.Star => PowerShellWasmBinaryOperator.Multiply,
            PowerShellWasmTokenKind.Slash => PowerShellWasmBinaryOperator.Divide,
            PowerShellWasmTokenKind.Remainder => PowerShellWasmBinaryOperator.Remainder,
            PowerShellWasmTokenKind.QuestionQuestion => PowerShellWasmBinaryOperator.NullCoalesce,
            _ => default
        };

        return kind is PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus or PowerShellWasmTokenKind.Star or
            PowerShellWasmTokenKind.Slash or PowerShellWasmTokenKind.Remainder or PowerShellWasmTokenKind.QuestionQuestion;
    }

    private static bool IsKeyword(IReadOnlyList<PowerShellWasmToken> tokens, int position, string keyword) =>
        position < tokens.Count &&
        tokens[position].Kind == PowerShellWasmTokenKind.Identifier &&
        !tokens[position].IsEscapedText &&
        tokens[position].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static PowerShellWasmTokenKind CurrentKind(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        position < tokens.Count ? tokens[position].Kind : PowerShellWasmTokenKind.EndOfInput;

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
        var tryStart = FindTryStatementStart(tokens, start);
        if (tryStart >= 0) return ReadTryStatementTokens(tokens, ref position, tryStart);
        var depth = 0;
        // FunctionDeclarationRule skips newlines throughout the header, including before an optional signature and body.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L5316-L5446
        var leadingFunction = IsKeyword(tokens, start, "function");
        var functionBodyStarted = false;
        var leadingParamBlock = IsKeyword(tokens, start, "param") &&
            CurrentKind(tokens, start + 1) == PowerShellWasmTokenKind.LParen;
        var leadingMetadataAttribute =
            TryReadBracketAnnotation(tokens, start, out var annotation, out _) &&
            IsScriptMetadataAttributeAnnotation(annotation);

        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Kind == PowerShellWasmTokenKind.EndOfInput)
            {
                break;
            }

            if (depth == 0 && token.Kind is PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon)
            {
                if (token.Kind == PowerShellWasmTokenKind.NewLine &&
                    (leadingFunction && !functionBodyStarted || IsTryContinuation(tokens, start, position) ||
                        IsIfContinuation(tokens, start, position) ||
                        !IsStatementBoundaryNewLine(tokens, position, start)))
                {
                    position++;
                    continue;
                }

                break;
            }

            if (leadingFunction && depth == 0 && token.Kind == PowerShellWasmTokenKind.LBrace) functionBodyStarted = true;
            UpdateDepth(token, ref depth);
            position++;

            if (leadingParamBlock && depth == 0 && token.Kind == PowerShellWasmTokenKind.RParen)
            {
                break;
            }

            if (leadingMetadataAttribute && depth == 0 && token.Kind == PowerShellWasmTokenKind.RBracket)
            {
                break;
            }
        }

        return RemoveTopLevelNewLines(tokens.Skip(start).Take(position - start).ToArray());
    }

    private static bool IsCatchClauseStart(IReadOnlyList<PowerShellWasmToken> tokens, int position) =>
        IsKeyword(tokens, position, "catch") &&
        !(CurrentKind(tokens, position + 1) == PowerShellWasmTokenKind.LBracket &&
            Contiguous(tokens[position], tokens[position + 1]));

    private static int FindTryStatementStart(IReadOnlyList<PowerShellWasmToken> tokens, int start)
    {
        if (IsKeyword(tokens, start, "try")) return start;
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (depth == 0 && tokens[i].Kind is PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon or
                PowerShellWasmTokenKind.EndOfInput or PowerShellWasmTokenKind.Pipe) break;
            if (depth == 0 && tokens[i].Kind == PowerShellWasmTokenKind.Equals)
            {
                var next = i + 1;
                while (CurrentKind(tokens, next) == PowerShellWasmTokenKind.NewLine) next++;
                if (!IsKeyword(tokens, next, "try")) return -1;
                // Equals inside a command argument (for example, label=try) is not statement assignment.
                try
                {
                    var target = new ExpressionParser(tokens.Skip(start).Take(i - start).ToArray()).Parse(requireComplete: true);
                    return IsSimpleAssignmentTarget(target) ? next : -1;
                }
                catch (InvalidOperationException) { return -1; }
            }
            UpdateDepth(tokens[i], ref depth);
        }
        return -1;
    }

    // A completed try/catch/finally is a statement boundary even without a semicolon.
    // Newlines may separate a clause keyword, its filters, and its body; semicolons may not.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L5494-L5669
    private static IReadOnlyList<PowerShellWasmToken> ReadTryStatementTokens(
        IReadOnlyList<PowerShellWasmToken> tokens, ref int position, int tryStart)
    {
        var start = position;
        position = tryStart;
        var depth = 0;
        var inFinally = false;
        while (position < tokens.Count && tokens[position].Kind != PowerShellWasmTokenKind.EndOfInput)
        {
            var token = tokens[position];
            if (depth == 0 && token.Kind == PowerShellWasmTokenKind.Semicolon) break;
            if (depth == 0 && IsKeyword(tokens, position, "finally")) inFinally = true;
            UpdateDepth(token, ref depth);
            position++;
            if (depth != 0 || token.Kind != PowerShellWasmTokenKind.RBrace) continue;

            var next = position;
            while (CurrentKind(tokens, next) == PowerShellWasmTokenKind.NewLine) next++;
            if (!inFinally && (IsCatchClauseStart(tokens, next) || IsKeyword(tokens, next, "finally")))
            {
                position = next;
                continue;
            }
            break;
        }
        return RemoveTopLevelNewLines(tokens.Skip(start).Take(position - start).ToArray());
    }

    private static IReadOnlyList<PowerShellWasmToken> RemoveTopLevelNewLines(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var result = new List<PowerShellWasmToken>();
        var depth = 0;

        foreach (var token in tokens)
        {
            if (token.Kind == PowerShellWasmTokenKind.NewLine && depth == 0)
            {
                continue;
            }

            result.Add(token);
            UpdateDepth(token, ref depth);
        }

        return result;
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

    private static bool IsIfContinuation(IReadOnlyList<PowerShellWasmToken> tokens, int start, int separatorPosition)
    {
        if (!IsKeyword(tokens, start, "if") ||
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

            return IsKeyword(tokens, i, "elseif") || IsKeyword(tokens, i, "else");
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

    private static bool IsPipelineSeparator(PowerShellWasmTokenKind kind) =>
        kind is PowerShellWasmTokenKind.Pipe or PowerShellWasmTokenKind.PipelineChainAnd or PowerShellWasmTokenKind.PipelineChainOr;

    private static int FindAssignmentEquals(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (depth == 0 && IsPipelineSeparator(tokens[i].Kind))
            {
                break;
            }

            if (depth == 0 && tokens[i].Kind == PowerShellWasmTokenKind.Equals)
            {
                return i;
            }

            UpdateDepth(tokens[i], ref depth);
        }

        return -1;
    }

    private static bool TryReadBracketAnnotation(
        IReadOnlyList<PowerShellWasmToken> tokens,
        int startPosition,
        out string annotation,
        out int afterPosition)
    {
        annotation = string.Empty;
        afterPosition = startPosition;
        if (CurrentKind(tokens, startPosition) != PowerShellWasmTokenKind.LBracket)
        {
            return false;
        }

        var position = startPosition + 1;
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (tokens[position].Kind == PowerShellWasmTokenKind.RBracket && depth == 0)
            {
                annotation = string.Concat(tokens.Skip(start).Take(position - start).Select(static token => token.Text));
                afterPosition = position + 1;
                return !string.IsNullOrWhiteSpace(annotation);
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        return false;
    }

    private static void UpdateDepth(PowerShellWasmToken token, ref int depth)
    {
        depth += token.Kind switch
        {
            PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or PowerShellWasmTokenKind.AtLBrace or
                PowerShellWasmTokenKind.AtLParen or PowerShellWasmTokenKind.DollarLParen or
                PowerShellWasmTokenKind.LBracket => 1,
            PowerShellWasmTokenKind.RParen or PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.RBracket => -1,
            _ => 0
        };
    }

    private sealed class ExpressionParser(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        private int _position;
        private bool _requireComplete;
        private bool _disableCommaOperator;

        public ExpressionAst Parse(bool requireComplete = false)
        {
            _requireComplete = requireComplete;
            if (tokens.Count == 0)
            {
                if (requireComplete)
                {
                    throw new InvalidOperationException("Expected an expression.");
                }

                return new StringExpressionAst(string.Empty, IsExpandable: false);
            }

            var expression = ParseAssignment();
            if (requireComplete && Current.Kind != PowerShellWasmTokenKind.EndOfInput)
            {
                throw new InvalidOperationException($"Unexpected token '{Current.Text}' in parenthesized expression.");
            }

            return expression;
        }

        // Native comma arrays bind more tightly than every binary operator, but less tightly than unary operators.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L6798-L7009
        private ExpressionAst ParseArrayLiteral()
        {
            var items = new List<ExpressionAst> { ParseUnary() };
            while (!_disableCommaOperator && Current.Kind == PowerShellWasmTokenKind.Comma)
            {
                _position++;
                items.Add(ParseUnary());
            }

            return items.Count > 1 ? new ArrayExpressionAst(items) : items[0];
        }

        private ExpressionAst ParseAssignment()
        {
            if (Current.Kind == PowerShellWasmTokenKind.Variable &&
                TryMapCompoundAssignmentOperator(Peek(1).Kind, out var compoundOperator) &&
                Peek(2).Kind == PowerShellWasmTokenKind.Equals)
            {
                var variable = Current.Text;
                _position += 3;
                return new CompoundAssignmentExpressionAst(variable, compoundOperator, ParseAssignment());
            }

            if (Current.Kind == PowerShellWasmTokenKind.Variable && Peek(1).Kind == PowerShellWasmTokenKind.Equals)
            {
                var variable = Current.Text;
                _position += 2;
                return new AssignmentExpressionAst(variable, ParseAssignment());
            }

            var target = ParseTernaryExpression();
            if (Current.Kind == PowerShellWasmTokenKind.Equals && IsSimpleAssignmentTarget(target))
            {
                _position++;
                return new SettableAssignmentExpressionAst(target, ParseAssignment());
            }

            return target;
        }

        private ExpressionAst ParseTernaryExpression()
        {
            var condition = ParseBinaryExpression(1);
            if (!IsTernaryQuestion())
            {
                return condition;
            }

            _position++;
            var ifTrue = ParseAssignment();
            Consume(PowerShellWasmTokenKind.Colon);
            var ifFalse = ParseAssignment();
            return new TernaryExpressionAst(condition, ifTrue, ifFalse);
        }

        private ExpressionAst ParseBinaryExpression(int minimumPrecedence)
        {
            var expression = ParseArrayLiteral();
            while (Current.Kind.IsBinaryOperator())
            {
                var precedence = Current.Kind.GetBinaryPrecedence();
                if (precedence < minimumPrecedence)
                {
                    break;
                }

                var operatorKind = Current.Kind;
                _position++;
                var op = MapBinaryOperator(operatorKind);
                var right = ParseBinaryExpression(precedence + 1);
                expression = new BinaryExpressionAst(expression, op, right);
            }

            return expression;
        }

        private ExpressionAst ParseUnary()
        {
            if (Current.Kind == PowerShellWasmTokenKind.Comma)
            {
                if (_disableCommaOperator) throw new InvalidOperationException("Expected an expression before ','.");
                _position++;
                return new ArrayExpressionAst([ParseUnary()]);
            }

            if (TryParseCastExpression(out var cast))
            {
                return cast;
            }

            if (Current.Kind is PowerShellWasmTokenKind.PlusPlus or PowerShellWasmTokenKind.MinusMinus)
            {
                var delta = Current.Kind == PowerShellWasmTokenKind.PlusPlus ? 1 : -1;
                _position++;
                var target = ParseUnary();
                if (!IsSettableAssignmentTarget(target))
                {
                    throw new InvalidOperationException("Increment and decrement operators require a variable, member, or index target.");
                }

                return new IncrementExpressionAst(target, delta, IsPrefix: true);
            }

            // An adjacent sign belongs to the literal before type inference and member access. Spaced +/- remain operators.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7058-L7088
            if (Current.Kind is PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus &&
                Peek(1).Kind == PowerShellWasmTokenKind.Number && Contiguous(Current, Peek(1)))
            {
                var literal = ParseNumber(Current.Text + Peek(1).Text);
                _position += 2;
                return ParsePostfix(literal);
            }

            if (Current.Kind.IsUnaryOperator())
            {
                var op = MapUnaryOperator(Current.Kind);
                _position++;
                return new UnaryExpressionAst(op, ParseUnary());
            }

            return ParsePostfix();
        }

        private bool TryParseCastExpression(out CastExpressionAst cast)
        {
            cast = null!;
            if (Current.Kind != PowerShellWasmTokenKind.LBracket ||
                !TryReadTypeName(_position, out var typeName, out var afterTypePosition) ||
                !IsCastOperandStart(TokenAt(afterTypePosition)))
            {
                return false;
            }

            _position = afterTypePosition;
            if (typeName.Equals("ordered", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The ordered attribute is valid only on a hashtable literal.");
            cast = new CastExpressionAst(typeName, ParseUnary());
            return true;
        }

        private ExpressionAst ParsePostfix(ExpressionAst? initialExpression = null)
        {
            var expression = initialExpression ?? ParsePrimary();
            while (true)
            {
                if (Current.Kind == PowerShellWasmTokenKind.Identifier &&
                    Current.Text.StartsWith(".", StringComparison.Ordinal) &&
                    Current.Text.Length > 1)
                {
                    if (Current.IsEscapedText) throw new InvalidOperationException("Escaped member names must be quoted.");
                    NormalizeSimpleMemberToken(preserveLeadingDot: true);
                    if (Current.Text.EndsWith(".", StringComparison.Ordinal))
                    {
                        foreach (var memberName in ReadMemberPath(".", trimLeadingDot: true))
                            expression = new MemberAccessExpressionAst(expression, memberName);
                    }
                    else
                    {
                        expression = new MemberAccessExpressionAst(expression, Current.Text[1..]);
                        _position++;
                    }
                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.Identifier &&
                    Current.Text.Equals(".", StringComparison.Ordinal))
                {
                    if (Current.HasLeadingWhitespace)
                        throw new InvalidOperationException("Member access requires '.' immediately after its target.");
                    _position++;
                    if (Current.Kind == PowerShellWasmTokenKind.Identifier)
                    {
                        foreach (var memberName in ReadMemberPath("."))
                            expression = new MemberAccessExpressionAst(expression, memberName);
                    }
                    else expression = new ComputedMemberAccessExpressionAst(expression, ParseComputedMemberName());
                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.DoubleColon)
                {
                    _position++;
                    var memberPath = ReadMemberPath("::");
                    expression = new StaticMemberAccessExpressionAst(expression, memberPath[0]);
                    foreach (var memberName in memberPath.Skip(1))
                    {
                        expression = new MemberAccessExpressionAst(expression, memberName);
                    }

                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.QuestionDot)
                {
                    if (_position == 0 || !HasMemberAccessAdjacency(tokens[_position - 1], Current))
                        throw new InvalidOperationException("Member access requires '?.' immediately after its target.");
                    _position++;
                    if (Current.Kind is PowerShellWasmTokenKind.Variable or PowerShellWasmTokenKind.StringLiteral or
                        PowerShellWasmTokenKind.ExpandableStringLiteral or PowerShellWasmTokenKind.LParen)
                    {
                        expression = new ComputedMemberAccessExpressionAst(expression, ParseComputedMemberName())
                            { NullConditional = true };
                        continue;
                    }
                    var memberPath = ReadMemberPath("?.");
                    expression = new NullConditionalMemberAccessExpressionAst(expression, memberPath[0]);
                    foreach (var memberName in memberPath.Skip(1))
                    {
                        expression = new MemberAccessExpressionAst(expression, memberName);
                    }

                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.AtLBrace &&
                    expression is TypeLiteralExpressionAst typeLiteral)
                {
                    _position++;
                    expression = new TypedHashtableExpressionAst(typeLiteral.TypeName, ParseHashtable());
                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.LParen &&
                    expression is MemberAccessExpressionAst or ComputedMemberAccessExpressionAst or
                        NullConditionalMemberAccessExpressionAst or StaticMemberAccessExpressionAst)
                {
                    expression = new MethodInvocationExpressionAst(expression, ParseArgumentList());
                    continue;
                }

                if (IsNullConditionalIndexOperator(tokens, _position) && _position > 0 &&
                    HasMemberAccessAdjacency(tokens[_position - 1], Current))
                {
                    _position++;
                    expression = ParseIndex(expression, nullConditional: true);
                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.LBracket)
                {
                    expression = ParseIndex(expression);
                    continue;
                }

                if (Current.Kind is PowerShellWasmTokenKind.PlusPlus or PowerShellWasmTokenKind.MinusMinus)
                {
                    var delta = Current.Kind == PowerShellWasmTokenKind.PlusPlus ? 1 : -1;
                    _position++;
                    if (!IsSettableAssignmentTarget(expression))
                    {
                        throw new InvalidOperationException("Increment and decrement operators require a variable, member, or index target.");
                    }

                    return new IncrementExpressionAst(expression, delta, IsPrefix: false);
                }

                return expression;
            }
        }

        private ExpressionAst ParsePrimary()
        {
            var token = Current;
            _position++;

            return token.Kind switch
            {
                PowerShellWasmTokenKind.Number => ParseNumber(token.Text),
                PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral => ParseStringToken(token),
                PowerShellWasmTokenKind.Variable => ParseVariable(token.Text),
                PowerShellWasmTokenKind.Identifier => new BareWordExpressionAst(token.Text),
                PowerShellWasmTokenKind.Parameter => new BareWordExpressionAst("-" + token.Text),
                PowerShellWasmTokenKind.LParen => ParseParenthesized(),
                PowerShellWasmTokenKind.LBrace => ParseScriptBlock(),
                PowerShellWasmTokenKind.AtLBrace => ParseHashtable(),
                PowerShellWasmTokenKind.AtLParen => ParseArray(),
                PowerShellWasmTokenKind.DollarLParen => ParseSubexpression(),
                PowerShellWasmTokenKind.LBracket => ParseTypeLiteral(),
                _ when _requireComplete => throw new InvalidOperationException($"Expected an expression, found '{token.Text}'."),
                _ => new BareWordExpressionAst(token.Text)
            };
        }

        private ExpressionAst ParseParenthesized()
        {
            var start = _position;
            var depth = 0;

            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.RParen && depth == 0)
                {
                    break;
                }

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var expressionTokens = tokens.Skip(start).Take(_position - start).ToArray();
            Consume(PowerShellWasmTokenKind.RParen);
            var expression = ParseParenthesizedExpressionValue(expressionTokens);
            return new ParenthesizedExpressionAst(expression);
        }

        private TypeLiteralExpressionAst ParseTypeLiteral() =>
            TryReadTypeName(_position - 1, out var typeName, out _position)
                ? new TypeLiteralExpressionAst(typeName)
                : throw new InvalidOperationException("Expected a type name inside '[]'.");

        private IReadOnlyList<ExpressionAst> ParseArgumentList()
        {
            Consume(PowerShellWasmTokenKind.LParen);
            var arguments = new List<ExpressionAst>();
            // Method commas separate arguments. Parenthesized expressions create their own parser and may contain arrays.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7890-L7930
            var previousDisableComma = _disableCommaOperator;
            _disableCommaOperator = true;
            try
            {
                while (Current.Kind is not PowerShellWasmTokenKind.RParen and not PowerShellWasmTokenKind.EndOfInput)
                {
                    if (Current.Kind == PowerShellWasmTokenKind.Comma)
                    {
                        _position++;
                        continue;
                    }

                    arguments.Add(ParseAssignment());
                    if (Current.Kind == PowerShellWasmTokenKind.Comma)
                    {
                        _position++;
                    }
                }

                Consume(PowerShellWasmTokenKind.RParen);
                return arguments;
            }
            finally { _disableCommaOperator = previousDisableComma; }
        }

        private ExpressionAst ParseComputedMemberName()
        {
            if (Current.Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral)
            {
                var token = Current;
                _position++;
                return ParseStringToken(token);
            }

            if (Current.Kind == PowerShellWasmTokenKind.Variable)
            {
                var token = Current;
                _position++;
                return ParseVariable(token.Text);
            }

            if (Current.Kind == PowerShellWasmTokenKind.LParen)
            {
                _position++;
                return ParseParenthesized();
            }

            throw new InvalidOperationException("Expected a quoted string, variable, or parenthesized expression after '.'.");
        }

        // Native ElementAccessRule preserves whether the operator was '[' or '?[' on the index AST.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7970-L8020
        private IndexExpressionAst ParseIndex(ExpressionAst target, bool nullConditional = false)
        {
            Consume(PowerShellWasmTokenKind.LBracket);
            var start = _position;
            var depth = 0;
            while (Current.Kind != PowerShellWasmTokenKind.EndOfInput)
            {
                if (depth == 0 && Current.Kind == PowerShellWasmTokenKind.RBracket) break;
                UpdateDepth(Current, ref depth);
                _position++;
            }
            var indexTokens = RemoveTopLevelNewLines(tokens.Skip(start).Take(_position - start).ToArray());
            Consume(PowerShellWasmTokenKind.RBracket);
            if (indexTokens.Count == 0) throw new InvalidOperationException("Expected an expression inside index brackets.");
            // Preserve direct comma-array indexes separately from parenthesized enumerable indexes and unary comma.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6597-L6620
            var parser = new ExpressionParser(indexTokens) { _requireComplete = true };
            var index = parser.ParseTernaryExpression();
            if (parser.Current.Kind != PowerShellWasmTokenKind.EndOfInput)
                throw new InvalidOperationException($"Unexpected token '{parser.Current.Text}' in index expression.");
            return new IndexExpressionAst(target, index) { NullConditional = nullConditional };
        }

        // MemberNameRule accepts a simple name after a member operator, including one separated by a newline.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7694-L7774
        private IReadOnlyList<string> ReadMemberPath(string operatorText, bool trimLeadingDot = false)
        {
            var memberPath = new List<string>();
            bool continues;
            do
            {
                if (Current.IsEscapedText) throw new InvalidOperationException("Escaped member names must be quoted.");
                NormalizeSimpleMemberToken(preserveLeadingDot: trimLeadingDot);
                if (Current.Kind != PowerShellWasmTokenKind.Identifier || Current.Text.Length == 0 ||
                    !trimLeadingDot && Current.Text.StartsWith(".", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Expected a member name after '{operatorText}'.");

                var text = trimLeadingDot ? Current.Text[1..] : Current.Text;
                memberPath.AddRange(text.Split('.', StringSplitOptions.RemoveEmptyEntries));
                continues = text.EndsWith(".", StringComparison.Ordinal);
                trimLeadingDot = false;
                _position++;
            } while (continues);
            return memberPath;
        }

        // Native MemberNameRule requests simple-name tokenization only after the parser enters member access.
        // Reuse dotted-name lexing here, so literal command arguments and following statements keep generic word rules.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L2161-L2189
        private void NormalizeSimpleMemberToken(bool preserveLeadingDot = false)
        {
            var original = Current;
            if (original.Kind != PowerShellWasmTokenKind.Identifier ||
                !original.Text.Contains('?') && !original.Text.Contains("<#", StringComparison.Ordinal)) return;
            var prefixLength = preserveLeadingDot ? 0 : 1;
            var normalized = PowerShellWasmTokenizer.Tokenize((preserveLeadingDot ? "" : ".") + original.Text, simpleMemberContext: true)
                .Where(static token => token.Kind != PowerShellWasmTokenKind.EndOfInput)
                .Select(token => token with
                {
                    Offset = original.Offset + token.Offset - prefixLength,
                    SourceScript = original.SourceScript,
                    IsInCatch = original.IsInCatch,
                    IsInFinally = original.IsInFinally
                }).ToArray();
            normalized[0] = normalized[0] with
            {
                Text = normalized[0].Text[prefixLength..],
                Offset = original.Offset,
                Length = normalized[0].Length - prefixLength,
                HasLeadingWhitespace = original.HasLeadingWhitespace
            };
            tokens = tokens.Take(_position).Concat(normalized).Concat(tokens.Skip(_position + 1)).ToArray();
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
            var openingToken = tokens[start - 1];
            var closingToken = Current;
            Consume(PowerShellWasmTokenKind.RBrace);
            var sourceText = openingToken.SourceScript?[(openingToken.Offset + openingToken.Length)..closingToken.Offset];
            return new ScriptBlockExpressionAst(ParseScript(MarkInsideFinally(bodyTokens, false))) { SourceText = sourceText };
        }

        private HashtableExpressionAst ParseHashtable()
        {
            var entries = new List<HashtableEntryAst>();
            var constantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (Current.Kind == PowerShellWasmTokenKind.NewLine) _position++;
            while (Current.Kind is not PowerShellWasmTokenKind.RBrace and not PowerShellWasmTokenKind.EndOfInput)
            {
                var key = ReadHashtableKeyExpression();
                // Native constant-key validation compares textual keys; parenthesized/dynamic keys validate at runtime.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/SemanticChecks.cs#L1112-L1137
                var constantKey = key switch
                {
                    BareWordExpressionAst word => word.Value,
                    StringExpressionAst text when !text.IsExpandable || !text.Value.Contains('$') => text.Value,
                    NumberExpressionAst number => Convert.ToString(number.Value, CultureInfo.InvariantCulture),
                    _ => null
                };
                if (constantKey is not null && !constantKeys.Add(constantKey))
                    throw new InvalidOperationException($"Duplicate key '{constantKey}' in hashtable literal.");
                Consume(PowerShellWasmTokenKind.Equals);
                entries.Add(new(key, ReadHashtableValueExpression()));
                while (Current.Kind is PowerShellWasmTokenKind.Semicolon or PowerShellWasmTokenKind.NewLine) _position++;
            }

            Consume(PowerShellWasmTokenKind.RBrace);
            return new HashtableExpressionAst(entries);
        }

        // A hash entry's RHS is one statement, including pipelines and control-flow output; commas belong to its value.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Parser.cs#L7341-L7461
        // Adapt the upstream grammar to PSWasm's expression-valued entries without its error-recovery machinery.
        private ExpressionAst ReadHashtableValueExpression()
        {
            while (Current.Kind == PowerShellWasmTokenKind.NewLine) _position++;
            var start = _position;
            var depth = 0;
            while (Current.Kind != PowerShellWasmTokenKind.EndOfInput)
            {
                if (depth == 0)
                {
                    if (Current.Kind is PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.Semicolon) break;
                    if (Current.Kind == PowerShellWasmTokenKind.NewLine &&
                        IsStatementBoundaryNewLine(tokens, _position, start) &&
                        !IsTryContinuation(tokens, start, _position) && !IsIfContinuation(tokens, start, _position))
                    {
                        var next = NextSignificantToken(tokens, _position + 1);
                        var doContinuation = IsKeyword(tokens, start, "do") && next is { Kind: PowerShellWasmTokenKind.Identifier } &&
                            (next.Value.Text.Equals("while", StringComparison.OrdinalIgnoreCase) ||
                                next.Value.Text.Equals("until", StringComparison.OrdinalIgnoreCase));
                        if (!doContinuation) break;
                    }
                }
                UpdateDepth(Current, ref depth);
                _position++;
            }

            var valueTokens = RemoveTopLevelNewLines(tokens.Skip(start).Take(_position - start).ToArray());
            if (valueTokens.Count == 0) throw new InvalidOperationException("Expected a statement after '=' in hashtable literal.");
            var statement = ParseStatement(valueTokens, requireCompleteExpressions: true);
            // The compiler requests assignment values in hash entries, just as it does for grouped assignments.
            if (!IsCommandSegment(valueTokens) && FindAssignmentEquals(valueTokens) > 0)
                return ParseParenthesizedExpressionValue(valueTokens);
            return statement is ExpressionStatementAst expression ? expression.Expression : new StatementExpressionAst(statement);
        }

        private ExpressionAst ParseArray()
        {
            var start = _position;
            var depth = 0;
            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.RParen && depth == 0)
                {
                    break;
                }

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var scriptTokens = tokens.Skip(start).Take(_position - start).ToArray();
            Consume(PowerShellWasmTokenKind.RParen);
            return new ArraySubexpressionAst(ParseScript(scriptTokens));
        }

        private ExpressionAst ParseSubexpression()
        {
            var start = _position;
            var depth = 0;
            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.RParen && depth == 0)
                {
                    break;
                }

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var scriptTokens = tokens.Skip(start).Take(_position - start).ToArray();
            Consume(PowerShellWasmTokenKind.RParen);
            return new SubexpressionAst(ParseScript(scriptTokens));
        }

        private ExpressionAst ReadHashtableKeyExpression()
        {
            if (Current.Kind == PowerShellWasmTokenKind.Comma)
                throw new InvalidOperationException("Expected a key expression in hashtable literal.");
            var start = _position;
            var depth = 0;

            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.Equals && depth == 0)
                {
                    break;
                }
                if (depth == 0 && Current.Kind is PowerShellWasmTokenKind.RBrace or PowerShellWasmTokenKind.Semicolon or PowerShellWasmTokenKind.NewLine)
                    throw new InvalidOperationException("Expected '=' after hashtable key.");

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var keyTokens = RemoveTopLevelNewLines(tokens.Skip(start).Take(_position - start).ToArray());
            return keyTokens.Count == 1 && keyTokens[0].Kind == PowerShellWasmTokenKind.Identifier
                ? new BareWordExpressionAst(keyTokens[0].Text)
                : new ExpressionParser(keyTokens).Parse(requireComplete: true);
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

        private PowerShellWasmToken Previous =>
            _position > 0 && _position - 1 < tokens.Count
                ? tokens[_position - 1]
                : new(PowerShellWasmTokenKind.EndOfInput, string.Empty, 0, 0, false);

        private PowerShellWasmToken TokenAt(int position) =>
            position < tokens.Count ? tokens[position] : new(PowerShellWasmTokenKind.EndOfInput, string.Empty, 0, 0, false);

        private bool IsTernaryQuestion() =>
            Current.Kind == PowerShellWasmTokenKind.Question &&
            (Current.HasLeadingWhitespace ||
                Previous.Kind is PowerShellWasmTokenKind.RParen or PowerShellWasmTokenKind.RBrace or
                    PowerShellWasmTokenKind.RBracket or PowerShellWasmTokenKind.StringLiteral or
                    PowerShellWasmTokenKind.ExpandableStringLiteral);

        private bool TryReadTypeName(int startPosition, out string typeName, out int afterTypePosition)
        {
            typeName = string.Empty;
            afterTypePosition = startPosition;
            if (TokenAt(startPosition).Kind != PowerShellWasmTokenKind.LBracket)
            {
                return false;
            }

            var position = startPosition + 1;
            var start = position;
            var depth = 0;

            while (position < tokens.Count)
            {
                if (tokens[position].Kind == PowerShellWasmTokenKind.RBracket && depth == 0)
                {
                    typeName = string.Concat(tokens.Skip(start).Take(position - start).Select(static token => token.Text));
                    afterTypePosition = position + 1;
                    return !string.IsNullOrWhiteSpace(typeName);
                }

                UpdateDepth(tokens[position], ref depth);
                position++;
            }

            return false;
        }

        private static bool IsCastOperandStart(PowerShellWasmToken token) =>
            !(token.Kind == PowerShellWasmTokenKind.Identifier && token.Text.StartsWith(".", StringComparison.Ordinal)) &&
            token.Kind is
                PowerShellWasmTokenKind.Number or
                PowerShellWasmTokenKind.StringLiteral or
                PowerShellWasmTokenKind.ExpandableStringLiteral or
                PowerShellWasmTokenKind.Variable or
                PowerShellWasmTokenKind.Identifier or
                PowerShellWasmTokenKind.LParen or
                PowerShellWasmTokenKind.LBrace or
                PowerShellWasmTokenKind.AtLParen or
                PowerShellWasmTokenKind.DollarLParen or
                PowerShellWasmTokenKind.LBracket or
                PowerShellWasmTokenKind.Plus or
                PowerShellWasmTokenKind.Minus or
                PowerShellWasmTokenKind.Not or
                PowerShellWasmTokenKind.Bnot;

        private static ExpressionAst ParseNumber(string text) =>
            new NumberExpressionAst(PowerShellWasmNumericLiteral.Parse(text));

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
                PowerShellWasmTokenKind.Iis => PowerShellWasmBinaryOperator.TypeIs,
                PowerShellWasmTokenKind.Inotis => PowerShellWasmBinaryOperator.TypeIsNot,
                PowerShellWasmTokenKind.Ias => PowerShellWasmBinaryOperator.TypeAs,
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
