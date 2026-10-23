namespace PSWasm.Language;

// Behavioral adaptation of PowerShell's variable-type and definite-assignment analysis, without CLR code generation.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/VariableAnalysis.cs#L586-L704
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/VariableAnalysis.cs#L844-L928
// Statically typed locals use ordinary cast conversions and return the converted assignment value; dynamic variables
// retain attribute-based conversion. Analyze only a new local scope, never a pipeline callback or a dotted invocation.
internal sealed class PowerShellWasmVariableAnalysis
{
    private sealed class Block
    {
        internal readonly List<Block> Next = [];
        internal readonly List<Block> Previous = [];
        internal readonly List<(string Name, string? Type)> Writes = [];
        internal HashSet<string>? Assigned;

        internal void Connect(Block target)
        {
            if (Next.Contains(target)) return;
            Next.Add(target);
            target.Previous.Add(this);
        }
    }

    private readonly Block _entry = new();
    private Block _current;
    private readonly Stack<(Block Break, Block Continue)> _loops = new();
    private readonly HashSet<string> _dynamic = new(StringComparer.OrdinalIgnoreCase);
    private bool _disabled;

    private PowerShellWasmVariableAnalysis() => _current = _entry;

    internal static IReadOnlyDictionary<string, string> Analyze(
        ScriptAst body, IReadOnlyList<ParameterDeclarationAst>? parameters = null)
    {
        var analysis = new PowerShellWasmVariableAnalysis();
        foreach (var parameter in parameters ?? body.Parameters)
        {
            analysis.Write(parameter.Name, parameter.TypeName);
            if (parameter.ValidateSet.Count > 0) analysis.MarkDynamic(parameter.Name);
        }
        analysis.Script(body);
        return analysis.Finish();
    }

    private IReadOnlyDictionary<string, string> Finish()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_disabled) return types;
        var visited = new HashSet<Block>();
        var blocks = new List<Block>();
        void Visit(Block block)
        {
            if (!visited.Add(block)) return;
            foreach (var next in block.Next) Visit(next);
            blocks.Add(block);
        }
        Visit(_entry);
        blocks.Reverse();
        foreach (var block in blocks)
        {
            var predecessors = block.Previous.Where(static previous => previous.Assigned is not null).ToArray();
            var assigned = predecessors.Length == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(predecessors[0].Assigned!, StringComparer.OrdinalIgnoreCase);
            foreach (var previous in predecessors.Skip(1)) assigned.IntersectWith(previous.Assigned!);
            foreach (var (name, type) in block.Writes)
            {
                if (!types.TryGetValue(name, out var existing)) types[name] = type ?? "object";
                else if (type is not null && !type.Equals(existing, StringComparison.OrdinalIgnoreCase) ||
                    type is null && !assigned.Contains(name) && existing != "object") _dynamic.Add(name);
                assigned.Add(name);
            }
            block.Assigned = assigned;
        }
        foreach (var name in _dynamic) types.Remove(name);
        return types;
    }

    private string? LocalName(string name)
    {
        if (name.StartsWith("private:", StringComparison.OrdinalIgnoreCase)) _disabled = true;
        if (name.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) name = name[6..];
        else if (name.Contains(':')) return null;
        if (name.Equals("PSItem", StringComparison.OrdinalIgnoreCase)) name = "_";
        return name.ToLowerInvariant() is "null" or "true" or "false" or "_" or "args" or "input" or
            "psboundparameters" or "error" or "?" or "foreach" or "switch" ? null : name;
    }

    private void MarkDynamic(string name)
    {
        if (LocalName(name) is { } local) _dynamic.Add(local);
    }

    private void Write(string name, string? type = null)
    {
        if (LocalName(name) is not { } local) return;
        string? normalized = null;
        if (type is not null)
        {
            normalized = PowerShellWasmAstExecutor.NormalizeCastTypeName(type);
            var scalar = normalized.EndsWith("[]", StringComparison.Ordinal) ? normalized[..^2] : normalized;
            if (scalar is not ("object" or "string" or "bool" or "int" or "long" or "double" or "decimal" or "byte" or "switch"))
            {
                try { normalized = PowerShellWasmDotNetBridge.ResolveType(type).FullName; }
                catch (InvalidOperationException) { _dynamic.Add(local); }
            }
        }
        _current.Writes.Add((local, normalized));
    }

    private void Target(ExpressionAst target)
    {
        while (target is ParenthesizedExpressionAst parenthesized) target = parenthesized.Expression;
        if (target is ArrayExpressionAst array) { foreach (var item in array.Items) Target(item); return; }
        string? type = null;
        var count = 0;
        while (target is CastExpressionAst cast) { type = cast.TypeName; count++; target = cast.Operand; }
        if (target is VariableExpressionAst variable && !variable.IsEnvironment)
        {
            Write(variable.Name, type);
            if (count > 1) MarkDynamic(variable.Name);
        }
        else Expression(target);
    }

    private void Script(ScriptAst script)
    {
        foreach (var statement in script.Statements) Statement(statement);
    }

    private void Statement(StatementAst statement)
    {
        switch (statement)
        {
            case AssignmentStatementAst s: Expression(s.Value); Write(s.VariableName); break;
            case SettableAssignmentStatementAst s: Expression(s.Value); Target(s.Target); break;
            case CompoundAssignmentStatementAst s: Expression(s.Value); Write(s.VariableName); break;
            case SettableCompoundAssignmentStatementAst s: Expression(s.Value); Target(s.Target); break;
            case ParallelAssignmentStatementAst s: Expression(s.Value); foreach (var name in s.VariableNames) Write(name); break;
            case VariableIncrementStatementAst s: LocalName(s.VariableName); break;
            case SettableIncrementStatementAst s: Expression(s.Target); break;
            case StatementAssignmentAst s: Statement(s.Statement); Write(s.VariableName); break;
            case SettableStatementAssignmentAst s: Statement(s.Statement); Target(s.Target); break;
            case ParallelStatementAssignmentAst s: Statement(s.Statement); foreach (var name in s.VariableNames) Write(name); break;
            case ExpressionStatementAst s: Expression(s.Expression); break;
            case CommandStatementAst s: Command(s.Command); break;
            case PipelineStatementAst s:
                foreach (var element in s.Elements)
                    if (element is ExpressionPipelineElementAst expression) Expression(expression.Expression);
                    else if (element is CommandPipelineElementAst command) Command(command.Command);
                break;
            case PipelineChainStatementAst s:
                Statement(s.First);
                foreach (var clause in s.Clauses) Optional(() => Statement(clause.Statement));
                break;
            case IfStatementAst s: If(s); break;
            case TryStatementAst s: Try(s); break;
            case WhileStatementAst s: Loop(() => Expression(s.Condition), () => Script(s.Body)); break;
            case ForStatementAst s:
                if (s.Initializer is not null) Statement(s.Initializer);
                Loop(s.Condition is null ? null : () => Expression(s.Condition), () => Script(s.Body), s.Iterator);
                break;
            case ForEachStatementAst s:
                var after = new Block();
                Loop(() => { Expression(s.Collection); _current.Connect(after); Write(s.VariableName); }, () => Script(s.Body));
                _current.Connect(after); _current = after;
                break;
            case DoWhileStatementAst s: DoLoop(s); break;
            case SwitchStatementAst s: Switch(s); break;
            case ReturnStatementAst s: if (s.Expression is not null) Expression(s.Expression); _current = new Block(); break;
            case ThrowStatementAst s: if (s.Expression is not null) Expression(s.Expression); _current = new Block(); break;
            case BreakStatementAst: if (_loops.TryPeek(out var breakLoop)) _current.Connect(breakLoop.Break); _current = new Block(); break;
            case ContinueStatementAst: if (_loops.TryPeek(out var continueLoop)) _current.Connect(continueLoop.Continue); _current = new Block(); break;
            case FunctionDefinitionStatementAst or ParamBlockStatementAst or MetadataAttributeStatementAst: break;
        }
    }

    private void If(IfStatementAst statement)
    {
        var after = new Block();
        foreach (var clause in statement.Clauses)
        {
            Expression(clause.Condition);
            var body = new Block(); var next = new Block();
            _current.Connect(body); _current.Connect(next); _current = body;
            Script(clause.Body); _current.Connect(after); _current = next;
        }
        if (statement.ElseBlock is not null) Script(statement.ElseBlock);
        _current.Connect(after); _current = after;
    }

    private void Try(TryStatementAst statement)
    {
        // As upstream does, catches start with the state before try: its first operation could throw.
        var before = _current; var body = new Block(); var after = new Block();
        var final = statement.FinallyBlock is null ? after : new Block();
        before.Connect(body); _current = body; Script(statement.TryBlock); _current.Connect(final);
        foreach (var catchBlock in statement.CatchBlocks)
        {
            var start = new Block(); before.Connect(start); _current = start;
            Script(catchBlock); _current.Connect(final);
        }
        if (statement.FinallyBlock is not null)
        {
            if (statement.CatchBlocks.Count == 0) before.Connect(final);
            _current = final; Script(statement.FinallyBlock); _current.Connect(after);
        }
        _current = after;
    }

    private void Loop(Action? condition, Action body, StatementAst? iterator = null)
    {
        var repeat = new Block(); var test = iterator is null ? repeat : new Block();
        _current.Connect(test);
        if (iterator is not null) { _current = repeat; Statement(iterator); _current.Connect(test); }
        _current = test;
        var start = new Block(); var after = new Block();
        if (condition is not null) { condition(); _current.Connect(after); }
        _loops.Push((after, repeat)); _current.Connect(start); _current = start;
        body(); _current.Connect(repeat); _loops.Pop(); _current = after;
    }

    private void DoLoop(DoWhileStatementAst statement)
    {
        var body = new Block(); var test = new Block(); var after = new Block();
        _current.Connect(body); _current = body; _loops.Push((after, test));
        Script(statement.Body); _current.Connect(test); _current = test; Expression(statement.Condition);
        _current.Connect(after); _current.Connect(body); _loops.Pop(); _current = after;
    }

    private void Switch(SwitchStatementAst statement)
    {
        Loop(() => Expression(statement.Input), () =>
        {
            var after = new Block();
            foreach (var clause in statement.Clauses)
            {
                Expression(clause.Pattern);
                var body = new Block(); var next = new Block();
                _current.Connect(next); _current.Connect(body); _current = body;
                Script(clause.Body); _current.Connect(after); _current.Connect(next); _current = next;
            }
            foreach (var defaultBlock in statement.DefaultBlocks) Script(defaultBlock);
            _current.Connect(after); _current = after;
        });
    }

    private void Optional(Action action)
    {
        var after = new Block(); var branch = new Block();
        _current.Connect(after); _current.Connect(branch); _current = branch;
        action(); _current.Connect(after); _current = after;
    }

    private void Command(CommandAst command)
    {
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/VariableAnalysis.cs#L279-L321
        var name = command.Name.ToLowerInvariant();
        if (name == "&") name = command.Arguments.FirstOrDefault()?.Value switch
        {
            BareWordExpressionAst target => target.Value.ToLowerInvariant(),
            StringExpressionAst target => target.Value.ToLowerInvariant(),
            _ => name
        };
        if (name.StartsWith("microsoft.powershell.utility\\", StringComparison.Ordinal)) name = name[29..];
        if (name is "." or "new-variable" or "remove-variable" or "set-variable" or "set-psbreakpoint" or
            "nv" or "rv" or "sv" or "set" or "sbp") _disabled = true;
        foreach (var parameter in command.Parameters) if (parameter.Value is not null) Expression(parameter.Value);
        foreach (var argument in command.Arguments) Expression(argument.Value);
        foreach (var loop in _loops) { _current.Connect(loop.Break); _current.Connect(loop.Continue); }
    }

    private void Expression(ExpressionAst expression)
    {
        switch (expression)
        {
            case VariableExpressionAst e: LocalName(e.Name); break;
            case AssignmentExpressionAst e: Expression(e.Value); Write(e.VariableName); break;
            case SettableAssignmentExpressionAst e: Expression(e.Value); Target(e.Target); break;
            case CompoundAssignmentExpressionAst e: Expression(e.Value); Write(e.VariableName); break;
            case SettableCompoundAssignmentExpressionAst e: Expression(e.Value); Target(e.Target); break;
            case ParallelAssignmentExpressionAst e: Expression(e.Value); foreach (var name in e.VariableNames) Write(name); break;
            case ParenthesizedExpressionAst e: Expression(e.Expression); break;
            case StatementExpressionAst e: Statement(e.Statement); break;
            case ScriptExpressionAst e: Script(e.Script); break;
            case ArraySubexpressionAst e: Script(e.Script); break;
            case SubexpressionAst e: Script(e.Script); break;
            case ArrayExpressionAst e: foreach (var item in e.Items) Expression(item); break;
            case HashtableExpressionAst e: foreach (var item in e.Entries) { Expression(item.Key); Expression(item.Value); } break;
            case TypedHashtableExpressionAst e: Expression(e.Hashtable); break;
            case CastExpressionAst e: Expression(e.Operand); break;
            case MemberAccessExpressionAst e: Expression(e.Target); break;
            case ComputedMemberAccessExpressionAst e: Expression(e.Target); Expression(e.MemberName); break;
            case NullConditionalMemberAccessExpressionAst e: Expression(e.Target); break;
            case StaticMemberAccessExpressionAst e: Expression(e.Target); break;
            case MethodInvocationExpressionAst e: Expression(e.Target); foreach (var argument in e.Arguments) Expression(argument); break;
            case IndexExpressionAst e: Expression(e.Target); Expression(e.Index); break;
            case UnaryExpressionAst e: Expression(e.Operand); break;
            case IncrementExpressionAst e: Expression(e.Target); break;
            case BinaryExpressionAst e:
                Expression(e.Left);
                if (e.Operator is PowerShellWasmBinaryOperator.LogicalAnd or PowerShellWasmBinaryOperator.LogicalOr)
                    Optional(() => Expression(e.Right));
                else Expression(e.Right);
                break;
            case TernaryExpressionAst e:
                Expression(e.Condition);
                var left = new Block(); var right = new Block(); var after = new Block();
                _current.Connect(left); _current.Connect(right); _current = left;
                Expression(e.IfTrue); _current.Connect(after); _current = right;
                Expression(e.IfFalse); _current.Connect(after); _current = after;
                break;
            case StringExpressionAst { IsExpandable: true } e:
                for (var index = 0; index < e.Value.Length; index++)
                {
                    if (e.Value[index] != '$' || e.LiteralDollarOffsets?.Contains(index) == true) continue;
                    if (PowerShellWasmParser.TryReadExpandableSubexpression(e.Value, index, out var script, out var next))
                    {
                        Script(new PowerShellWasmParser().Parse(script, e.IsInCatch, e.IsInFinally)); index = next - 1;
                    }
                    else if (e.Value.AsSpan(index + 1).StartsWith("private:", StringComparison.OrdinalIgnoreCase) ||
                        e.Value.AsSpan(index + 1).StartsWith("{private:", StringComparison.OrdinalIgnoreCase)) _disabled = true;
                }
                break;
            // Nested function and scriptblock scopes are analyzed separately when invoked.
            case ScriptBlockExpressionAst or StringExpressionAst or BareWordExpressionAst or NumberExpressionAst or TypeLiteralExpressionAst: break;
        }
    }
}
