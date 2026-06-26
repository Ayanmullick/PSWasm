using System.Globalization;

namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/Parser.cs
// Ternary reference: ExpressionRule handling for QuestionMark / Colon and TernaryExpressionAst construction.
// Null-conditional member reference: member-access parsing for the QuestionDot token.
// Null coalescing assignment reference: compound assignment parsing for the QuestionQuestion token followed by Equals.
// Browser note: this parser models a browser-safe PowerShell subset and produces the PSWasm AST profile.
public sealed class PowerShellWasmParser
{
    public ScriptAst Parse(string script)
    {
        var tokens = PowerShellWasmTokenizer.Tokenize(script);
        return ParseScript(tokens);
    }

    private static ScriptAst ParseScript(IReadOnlyList<PowerShellWasmToken> tokens)
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

    private static StatementAst ParseStatement(IReadOnlyList<PowerShellWasmToken> tokens)
    {
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

        if (IsKeyword(tokens, 0, "return"))
        {
            return new ReturnStatementAst(tokens.Count == 1 ? null : ParseExpression(tokens.Skip(1).ToArray()));
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

        var equals = FindTopLevel(tokens, PowerShellWasmTokenKind.Equals);
        if (tokens.Count > 0 && tokens[0].Kind == PowerShellWasmTokenKind.Variable && equals > 0)
        {
            var valueTokens = tokens.Skip(equals + 1).ToArray();
            if (TryReadAssignmentTargets(tokens, equals, out var variableNames))
            {
                if (variableNames.Count == 1)
                {
                    return IsStatementAssignmentValue(valueTokens)
                        ? new StatementAssignmentAst(variableNames[0], ParseStatement(valueTokens))
                        : new AssignmentStatementAst(variableNames[0], ParseExpression(valueTokens));
                }

                return IsStatementAssignmentValue(valueTokens)
                    ? new ParallelStatementAssignmentAst(variableNames, ParseStatement(valueTokens))
                    : new ParallelAssignmentStatementAst(variableNames, ParseExpression(valueTokens));
            }

            var target = ParseExpression(tokens.Take(equals).ToArray());
            if (!IsSettableAssignmentTarget(target))
            {
                throw new InvalidOperationException("Expected one or more assignment targets before '='.");
            }

            return IsStatementAssignmentValue(valueTokens)
                ? new SettableStatementAssignmentAst(target, ParseStatement(valueTokens))
                : new SettableAssignmentStatementAst(target, ParseExpression(valueTokens));
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

        if (TryParseVariableIncrementStatement(tokens, out var incrementStatement))
        {
            return incrementStatement;
        }

        if (TryParseSettableIncrementStatement(tokens, out var settableIncrementStatement))
        {
            return settableIncrementStatement;
        }

        if (IsCommandSegment(tokens))
        {
            return new CommandStatementAst(ParseCommand(tokens));
        }

        return new ExpressionStatementAst(ParseExpression(tokens));
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
            sections[1].Count == 0 ? null : ParseExpression(sections[1]),
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
        var matchMode = SwitchMatchMode.Wildcard;
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
        var body = ReadScriptBlock(tokens, ref position, "function");
        if (parameters.Count > 0 && body.Parameters.Count > 0)
        {
            throw new InvalidOperationException("Function parameters must be declared either after the function name or in a param block, not both.");
        }

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

    private static IReadOnlyList<ParameterDeclarationAst> ReadFunctionParameters(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var parameters = new List<ParameterDeclarationAst>();
        position++;

        while (position < tokens.Count && tokens[position].Kind != PowerShellWasmTokenKind.RParen)
        {
            if (tokens[position].Kind is PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.NewLine or PowerShellWasmTokenKind.Semicolon)
            {
                position++;
                continue;
            }

            parameters.Add(ReadParameterDeclaration(tokens, ref position));
        }

        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.RParen)
        {
            throw new InvalidOperationException("Expected ')' to close function parameter list.");
        }

        position++;
        return parameters;
    }

    private static ParameterDeclarationAst ReadParameterDeclaration(IReadOnlyList<PowerShellWasmToken> tokens, ref int position)
    {
        var annotations = new List<string>();
        while (CurrentKind(tokens, position) == PowerShellWasmTokenKind.LBracket)
        {
            annotations.Add(ReadParameterTypeName(tokens, ref position));
        }

        var typeName = annotations.Count > 0 && !IsParameterAttributeAnnotation(annotations[^1])
            ? annotations[^1]
            : null;

        if (position >= tokens.Count || tokens[position].Kind != PowerShellWasmTokenKind.Variable)
        {
            throw new InvalidOperationException("Function parameter lists support optional attributes and type literals followed by variable names.");
        }

        var name = tokens[position++].Text;
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

        return new ParameterDeclarationAst(name, typeName, defaultValue, ExtractParameterAliases(annotations), ExtractValidateSetValues(annotations));
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
        var start = position;
        var depth = 0;

        while (position < tokens.Count)
        {
            if (depth == 0 && tokens[position].Kind is PowerShellWasmTokenKind.Comma or PowerShellWasmTokenKind.RParen)
            {
                break;
            }

            UpdateDepth(tokens[position], ref depth);
            position++;
        }

        return RemoveTopLevelNewLines(tokens.Skip(start).Take(position - start).ToArray());
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
                return ParseExpression(expressionTokens);
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
                return ParseExpression(expressionTokens);
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
                return ParseScript(bodyTokens);
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

                var argumentTokens = ReadCommandArgument(tokens, ref position);
                parameters.Add(new(name, ParseParameterArgumentExpression(name, argumentTokens)));
                continue;
            }

            if (TryReadOperatorParameter(tokens, ref position, out var operatorParameter))
            {
                parameters.Add(operatorParameter);
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

        return new CommandAst(GetCommandName(tokens[0]), parameters, arguments);
    }

    private static bool TryReadOperatorParameter(
        IReadOnlyList<PowerShellWasmToken> tokens,
        ref int position,
        out CommandParameterAst parameter)
    {
        parameter = null!;
        if (!TryGetOperatorParameterName(tokens[position].Kind, out var parameterName))
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
        position < tokens.Count && TryGetOperatorParameterName(tokens[position].Kind, out _);

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
        var start = position;
        if (tokens[position].Kind is PowerShellWasmTokenKind.LParen or PowerShellWasmTokenKind.LBrace or
            PowerShellWasmTokenKind.AtLBrace or PowerShellWasmTokenKind.AtLParen or
            PowerShellWasmTokenKind.DollarLParen or PowerShellWasmTokenKind.LBracket)
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

        if (position < tokens.Count && tokens[position].Kind.IsBinaryOperator() && !IsOperatorParameter(tokens, position))
        {
            while (position < tokens.Count &&
                tokens[position].Kind != PowerShellWasmTokenKind.Parameter &&
                !IsSplatStart(tokens, position) &&
                !IsOperatorParameter(tokens, position))
            {
                position++;
            }
        }

        return tokens.Skip(start).Take(position - start).ToArray();
    }

    private static ExpressionAst ParseCommandArgumentExpression(IReadOnlyList<PowerShellWasmToken> tokens) =>
        IsBareWildcardArgument(tokens)
            ? new BareWordExpressionAst(string.Concat(tokens.Select(GetBareCommandTokenText)))
            : ParseExpression(tokens);

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

    private static bool IsBareWildcardArgument(IReadOnlyList<PowerShellWasmToken> tokens) =>
        tokens.Count > 1 &&
        tokens.Any(static token => token.Kind == PowerShellWasmTokenKind.Star) &&
        tokens.All(static token => token.Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Parameter or PowerShellWasmTokenKind.Star) &&
        tokens.Zip(tokens.Skip(1)).All(static pair => IsContiguousBareCommandToken(pair.First, pair.Second));

    private static string GetBareCommandTokenText(PowerShellWasmToken token) =>
        token.Kind == PowerShellWasmTokenKind.Parameter ? "-" + token.Text : token.Text;

    private static bool IsContiguousBareCommandToken(PowerShellWasmToken left, PowerShellWasmToken right) =>
        !right.HasLeadingWhitespace && right.Offset == left.Offset + left.Length &&
        right.Kind is PowerShellWasmTokenKind.Identifier or PowerShellWasmTokenKind.Parameter or PowerShellWasmTokenKind.Star;

    private static ExpressionAst ParseExpression(IReadOnlyList<PowerShellWasmToken> tokens)
    {
        if (TryParseSettableCompoundAssignmentExpression(tokens, out var assignment))
        {
            return assignment;
        }

        var parser = new ExpressionParser(tokens);
        return parser.Parse();
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
        if (HasTopLevelStatementSeparator(tokens))
        {
            return new ScriptExpressionAst(ParseScript(tokens));
        }

        var normalized = RemoveTopLevelNewLines(tokens);
        return IsStatementExpressionValue(normalized)
            ? new StatementExpressionAst(ParseStatement(normalized))
            : ParseExpression(normalized);
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

    private static bool IsStatementBoundaryNewLine(IReadOnlyList<PowerShellWasmToken> tokens, int position)
    {
        var previous = PreviousSignificantToken(tokens, position);
        var next = NextSignificantToken(tokens, position + 1);
        return previous.HasValue &&
            next.HasValue &&
            CanEndStatementBeforeNewLine(previous.Value.Kind) &&
            CanStartStatementAfterNewLine(next.Value.Kind);
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
            PowerShellWasmTokenKind.Colon or
            PowerShellWasmTokenKind.Comma);

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

    private static bool IsSettableAssignmentTarget(ExpressionAst target) =>
        target is VariableExpressionAst or MemberAccessExpressionAst or ComputedMemberAccessExpressionAst or IndexExpressionAst;

    private static bool TryFindTopLevelCompoundAssignment(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out int operatorPosition,
        out PowerShellWasmBinaryOperator op)
    {
        var depth = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
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

    private static bool TryParseVariableIncrementStatement(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out VariableIncrementStatementAst statement)
    {
        if (tokens.Count == 2 &&
            tokens[0].Kind == PowerShellWasmTokenKind.Variable)
        {
            if (tokens[1].Kind == PowerShellWasmTokenKind.PlusPlus)
            {
                statement = new VariableIncrementStatementAst(tokens[0].Text, 1);
                return true;
            }

            if (tokens[1].Kind == PowerShellWasmTokenKind.MinusMinus)
            {
                statement = new VariableIncrementStatementAst(tokens[0].Text, -1);
                return true;
            }
        }

        statement = null!;
        return false;
    }

    private static bool TryParseSettableIncrementStatement(
        IReadOnlyList<PowerShellWasmToken> tokens,
        out SettableIncrementStatementAst statement)
    {
        statement = null!;
        if (tokens.Count < 2)
        {
            return false;
        }

        var delta = tokens[^1].Kind switch
        {
            PowerShellWasmTokenKind.PlusPlus => 1,
            PowerShellWasmTokenKind.MinusMinus => -1,
            _ => 0
        };

        if (delta == 0)
        {
            return false;
        }

        var target = ParseExpression(tokens.Take(tokens.Count - 1).ToArray());
        if (!IsSettableAssignmentTarget(target))
        {
            return false;
        }

        statement = new SettableIncrementStatementAst(target, delta);
        return true;
    }

    private static bool IsKeyword(IReadOnlyList<PowerShellWasmToken> tokens, int position, string keyword) =>
        position < tokens.Count &&
        tokens[position].Kind == PowerShellWasmTokenKind.Identifier &&
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
        var depth = 0;
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

                if (IsIfContinuation(tokens, start, position))
                {
                    position++;
                    continue;
                }

                break;
            }

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
            if (Current.Kind == PowerShellWasmTokenKind.Equals && IsSettableAssignmentTarget(target))
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
            var ifTrue = ParseCommaExpression();
            Consume(PowerShellWasmTokenKind.Colon);
            var ifFalse = ParseCommaExpression();
            return new TernaryExpressionAst(condition, ifTrue, ifFalse);
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

                var operatorKind = Current.Kind;
                _position++;
                var op = MapBinaryOperator(operatorKind);
                var right = IsReplaceOperator(operatorKind)
                    ? ParseReplaceRightOperand(precedence + 1)
                    : ParseBinaryExpression(precedence + 1);
                expression = new BinaryExpressionAst(expression, op, right);
            }

            return expression;
        }

        private ExpressionAst ParseReplaceRightOperand(int minimumPrecedence)
        {
            var items = new List<ExpressionAst> { ParseBinaryExpression(minimumPrecedence) };
            while (Current.Kind == PowerShellWasmTokenKind.Comma)
            {
                _position++;
                items.Add(ParseBinaryExpression(minimumPrecedence));
            }

            return items.Count == 1 ? items[0] : new ArrayExpressionAst(items);
        }

        private static bool IsReplaceOperator(PowerShellWasmTokenKind kind) =>
            kind is PowerShellWasmTokenKind.Ireplace or PowerShellWasmTokenKind.Creplace;

        private ExpressionAst ParseUnary()
        {
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
            cast = new CastExpressionAst(typeName, ParseUnary());
            return true;
        }

        private ExpressionAst ParsePostfix()
        {
            var expression = ParsePrimary();
            while (true)
            {
                if (Current.Kind == PowerShellWasmTokenKind.Identifier &&
                    Current.Text.StartsWith(".", StringComparison.Ordinal) &&
                    Current.Text.Length > 1)
                {
                    expression = new MemberAccessExpressionAst(expression, Current.Text[1..]);
                    _position++;
                    continue;
                }

                if (Current.Kind == PowerShellWasmTokenKind.Identifier &&
                    Current.Text.Equals(".", StringComparison.Ordinal))
                {
                    _position++;
                    expression = new ComputedMemberAccessExpressionAst(expression, ParseComputedMemberName());
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
                    _position++;
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
                    expression is MemberAccessExpressionAst or NullConditionalMemberAccessExpressionAst or StaticMemberAccessExpressionAst)
                {
                    expression = new MethodInvocationExpressionAst(expression, ParseArgumentList());
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
                PowerShellWasmTokenKind.StringLiteral => new StringExpressionAst(token.Text, IsExpandable: false),
                PowerShellWasmTokenKind.ExpandableStringLiteral => new StringExpressionAst(token.Text, IsExpandable: true),
                PowerShellWasmTokenKind.Variable => ParseVariable(token.Text),
                PowerShellWasmTokenKind.Identifier => new BareWordExpressionAst(token.Text),
                PowerShellWasmTokenKind.Parameter => new BareWordExpressionAst("-" + token.Text),
                PowerShellWasmTokenKind.LParen => ParseParenthesized(),
                PowerShellWasmTokenKind.LBrace => ParseScriptBlock(),
                PowerShellWasmTokenKind.AtLBrace => ParseHashtable(),
                PowerShellWasmTokenKind.AtLParen => ParseArray(),
                PowerShellWasmTokenKind.DollarLParen => ParseSubexpression(),
                PowerShellWasmTokenKind.LBracket => ParseTypeLiteral(),
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

        private ExpressionAst ParseComputedMemberName()
        {
            if (Current.Kind is PowerShellWasmTokenKind.StringLiteral or PowerShellWasmTokenKind.ExpandableStringLiteral)
            {
                var token = Current;
                _position++;
                return new StringExpressionAst(token.Text, token.Kind == PowerShellWasmTokenKind.ExpandableStringLiteral);
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

        private IReadOnlyList<string> ReadMemberPath(string operatorText)
        {
            if (Current.Kind != PowerShellWasmTokenKind.Identifier ||
                Current.Text.Length == 0 ||
                Current.Text.StartsWith(".", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a member name after '{operatorText}'.");
            }

            var memberPath = Current.Text.Split('.', StringSplitOptions.RemoveEmptyEntries);
            _position++;
            return memberPath;
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
            return new ScriptBlockExpressionAst(ParseScript(bodyTokens));
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

                var key = ReadHashtableKeyExpression();
                Consume(PowerShellWasmTokenKind.Equals);
                entries.Add(new(key, ParseAssignment()));
                SkipExpressionSeparators();
            }

            Consume(PowerShellWasmTokenKind.RBrace);
            return new HashtableExpressionAst(entries);
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
            var start = _position;
            var depth = 0;

            while (Current.Kind is not PowerShellWasmTokenKind.EndOfInput)
            {
                if (Current.Kind == PowerShellWasmTokenKind.Equals && depth == 0)
                {
                    break;
                }

                UpdateDepth(Current, ref depth);
                _position++;
            }

            var keyTokens = RemoveTopLevelNewLines(tokens.Skip(start).Take(_position - start).ToArray());
            return keyTokens.Count == 1 && keyTokens[0].Kind == PowerShellWasmTokenKind.Identifier
                ? new BareWordExpressionAst(keyTokens[0].Text)
                : ParseExpression(keyTokens);
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
