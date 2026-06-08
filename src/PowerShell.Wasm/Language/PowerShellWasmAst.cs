namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/ast.cs
// Browser note: this is a small AST profile for PSWasm, not a verbatim copy of SMA's full AST hierarchy.
public abstract record PowerShellWasmAst;

public sealed record ScriptAst(IReadOnlyList<StatementAst> Statements) : PowerShellWasmAst;

public abstract record StatementAst : PowerShellWasmAst;

public sealed record AssignmentStatementAst(string VariableName, ExpressionAst Value) : StatementAst;

public sealed record ExpressionStatementAst(ExpressionAst Expression) : StatementAst;

public sealed record CommandStatementAst(CommandAst Command) : StatementAst;

public sealed record PipelineStatementAst(IReadOnlyList<PipelineElementAst> Elements) : StatementAst;

public abstract record PipelineElementAst : PowerShellWasmAst;

public sealed record ExpressionPipelineElementAst(ExpressionAst Expression) : PipelineElementAst;

public sealed record CommandPipelineElementAst(CommandAst Command) : PipelineElementAst;

public sealed record CommandAst(
    string Name,
    IReadOnlyList<CommandParameterAst> Parameters,
    IReadOnlyList<CommandArgumentAst> Arguments) : PowerShellWasmAst;

public sealed record CommandParameterAst(string Name, ExpressionAst? Value) : PowerShellWasmAst;

public sealed record CommandArgumentAst(ExpressionAst Value, bool IsSplat = false) : PowerShellWasmAst;

public abstract record ExpressionAst : PowerShellWasmAst;

public sealed record BareWordExpressionAst(string Value) : ExpressionAst;

public sealed record NumberExpressionAst(object Value) : ExpressionAst;

public sealed record StringExpressionAst(string Value, bool IsExpandable) : ExpressionAst;

public sealed record VariableExpressionAst(string Name, bool IsEnvironment) : ExpressionAst;

public sealed record AssignmentExpressionAst(string VariableName, ExpressionAst Value) : ExpressionAst;

public sealed record HashtableExpressionAst(IReadOnlyList<HashtableEntryAst> Entries) : ExpressionAst;

public sealed record HashtableEntryAst(string Key, ExpressionAst Value) : PowerShellWasmAst;

public sealed record ArrayExpressionAst(IReadOnlyList<ExpressionAst> Items) : ExpressionAst;

public sealed record ParenthesizedExpressionAst(ExpressionAst Expression) : ExpressionAst;

public sealed record UnaryExpressionAst(PowerShellWasmUnaryOperator Operator, ExpressionAst Operand) : ExpressionAst;

public sealed record BinaryExpressionAst(ExpressionAst Left, PowerShellWasmBinaryOperator Operator, ExpressionAst Right) : ExpressionAst;

public sealed record ComparisonExpressionAst(ExpressionAst Left, PowerShellWasmComparisonOperator Operator, ExpressionAst Right) : ExpressionAst;

public enum PowerShellWasmUnaryOperator
{
    Plus,
    Minus
}

public enum PowerShellWasmBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide
}

public enum PowerShellWasmComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}
