namespace PSWasm.Language;

// PowerShell source reference: src/System.Management.Automation/engine/parser/ast.cs
// Ternary reference: System.Management.Automation.Language.TernaryExpressionAst.
// Browser note: this is a small AST profile for PSWasm, not a verbatim copy of SMA's full AST hierarchy.
public abstract record PowerShellWasmAst;

public sealed record ScriptAst(IReadOnlyList<StatementAst> Statements, IReadOnlyList<ParameterDeclarationAst> Parameters) : PowerShellWasmAst
{
    public ScriptAst(IReadOnlyList<StatementAst> Statements)
        : this(Statements, [])
    {
    }
}

public abstract record StatementAst : PowerShellWasmAst;

public sealed record AssignmentStatementAst(string VariableName, ExpressionAst Value) : StatementAst;

public sealed record SettableAssignmentStatementAst(ExpressionAst Target, ExpressionAst Value) : StatementAst;

public sealed record CompoundAssignmentStatementAst(
    string VariableName,
    PowerShellWasmBinaryOperator Operator,
    ExpressionAst Value) : StatementAst;

public sealed record ParallelAssignmentStatementAst(IReadOnlyList<string> VariableNames, ExpressionAst Value) : StatementAst;

public sealed record VariableIncrementStatementAst(string VariableName, int Delta) : StatementAst;

public sealed record StatementAssignmentAst(string VariableName, StatementAst Statement) : StatementAst;

public sealed record SettableStatementAssignmentAst(ExpressionAst Target, StatementAst Statement) : StatementAst;

public sealed record ParallelStatementAssignmentAst(IReadOnlyList<string> VariableNames, StatementAst Statement) : StatementAst;

public sealed record ExpressionStatementAst(ExpressionAst Expression) : StatementAst;

public sealed record CommandStatementAst(CommandAst Command) : StatementAst;

public sealed record PipelineStatementAst(IReadOnlyList<PipelineElementAst> Elements) : StatementAst;

public sealed record PipelineChainStatementAst(
    StatementAst First,
    IReadOnlyList<PipelineChainClauseAst> Clauses) : StatementAst;

public sealed record PipelineChainClauseAst(PipelineChainOperator Operator, StatementAst Statement) : PowerShellWasmAst;

public sealed record TryStatementAst(
    ScriptAst TryBlock,
    IReadOnlyList<ScriptAst> CatchBlocks,
    ScriptAst? FinallyBlock) : StatementAst;

public sealed record IfStatementAst(
    IReadOnlyList<IfClauseAst> Clauses,
    ScriptAst? ElseBlock) : StatementAst;

public sealed record IfClauseAst(ExpressionAst Condition, ScriptAst Body) : PowerShellWasmAst;

public sealed record ForEachStatementAst(string VariableName, ExpressionAst Collection, ScriptAst Body) : StatementAst;

public sealed record WhileStatementAst(ExpressionAst Condition, ScriptAst Body) : StatementAst;

public sealed record DoWhileStatementAst(ScriptAst Body, ExpressionAst Condition, bool Until) : StatementAst;

public sealed record ForStatementAst(
    StatementAst? Initializer,
    ExpressionAst? Condition,
    StatementAst? Iterator,
    ScriptAst Body) : StatementAst;

public sealed record SwitchStatementAst(
    ExpressionAst Input,
    IReadOnlyList<SwitchClauseAst> Clauses,
    IReadOnlyList<ScriptAst> DefaultBlocks,
    bool UseRegex,
    bool CaseSensitive) : StatementAst;

public sealed record SwitchClauseAst(ExpressionAst Pattern, ScriptAst Body) : PowerShellWasmAst;

public sealed record FunctionDefinitionStatementAst(
    string Name,
    IReadOnlyList<ParameterDeclarationAst> Parameters,
    ScriptAst Body) : StatementAst;

public sealed record ParamBlockStatementAst(IReadOnlyList<ParameterDeclarationAst> Parameters) : StatementAst;

public sealed record ParameterDeclarationAst(
    string Name,
    string? TypeName,
    ExpressionAst? DefaultValue,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> ValidateSet) : PowerShellWasmAst;

public sealed record MetadataAttributeStatementAst(string Name) : StatementAst;

public sealed record ReturnStatementAst(ExpressionAst? Expression) : StatementAst;

public sealed record BreakStatementAst : StatementAst;

public sealed record ContinueStatementAst : StatementAst;

public abstract record PipelineElementAst : PowerShellWasmAst;

public sealed record ExpressionPipelineElementAst(ExpressionAst Expression) : PipelineElementAst;

public sealed record CommandPipelineElementAst(CommandAst Command) : PipelineElementAst;

public sealed record CommandAst(
    string Name,
    IReadOnlyList<CommandParameterAst> Parameters,
    IReadOnlyList<CommandArgumentAst> Arguments) : PowerShellWasmAst;

public sealed record CommandParameterAst(string Name, ExpressionAst? Value) : PowerShellWasmAst;

public sealed record CommandArgumentAst(ExpressionAst Value, bool IsSplat = false) : PowerShellWasmAst;

public enum PipelineChainOperator
{
    And,
    Or
}

public abstract record ExpressionAst : PowerShellWasmAst;

public sealed record BareWordExpressionAst(string Value) : ExpressionAst;

public sealed record NumberExpressionAst(object Value) : ExpressionAst;

public sealed record StringExpressionAst(string Value, bool IsExpandable) : ExpressionAst;

public sealed record VariableExpressionAst(string Name, bool IsEnvironment) : ExpressionAst;

public sealed record AssignmentExpressionAst(string VariableName, ExpressionAst Value) : ExpressionAst;

public sealed record SettableAssignmentExpressionAst(ExpressionAst Target, ExpressionAst Value) : ExpressionAst;

public sealed record CompoundAssignmentExpressionAst(
    string VariableName,
    PowerShellWasmBinaryOperator Operator,
    ExpressionAst Value) : ExpressionAst;

public sealed record HashtableExpressionAst(IReadOnlyList<HashtableEntryAst> Entries) : ExpressionAst;

public sealed record HashtableEntryAst(ExpressionAst Key, ExpressionAst Value) : PowerShellWasmAst;

public sealed record TypedHashtableExpressionAst(string TypeName, HashtableExpressionAst Hashtable) : ExpressionAst;

public sealed record ArrayExpressionAst(IReadOnlyList<ExpressionAst> Items) : ExpressionAst;

public sealed record ArraySubexpressionAst(ScriptAst Script) : ExpressionAst;

public sealed record ScriptBlockExpressionAst(ScriptAst Body) : ExpressionAst;

public sealed record ParenthesizedExpressionAst(ExpressionAst Expression) : ExpressionAst;

public sealed record StatementExpressionAst(StatementAst Statement) : ExpressionAst;

public sealed record ScriptExpressionAst(ScriptAst Script) : ExpressionAst;

public sealed record TypeLiteralExpressionAst(string TypeName) : ExpressionAst;

public sealed record CastExpressionAst(string TypeName, ExpressionAst Operand) : ExpressionAst;

public sealed record MemberAccessExpressionAst(ExpressionAst Target, string MemberName) : ExpressionAst;

public sealed record StaticMemberAccessExpressionAst(ExpressionAst Target, string MemberName) : ExpressionAst;

public sealed record MethodInvocationExpressionAst(ExpressionAst Target, IReadOnlyList<ExpressionAst> Arguments) : ExpressionAst;

public sealed record IndexExpressionAst(ExpressionAst Target, ExpressionAst Index) : ExpressionAst;

public sealed record UnaryExpressionAst(PowerShellWasmUnaryOperator Operator, ExpressionAst Operand) : ExpressionAst;

public sealed record TernaryExpressionAst(ExpressionAst Condition, ExpressionAst IfTrue, ExpressionAst IfFalse) : ExpressionAst;

public sealed record BinaryExpressionAst(ExpressionAst Left, PowerShellWasmBinaryOperator Operator, ExpressionAst Right) : ExpressionAst;

public enum PowerShellWasmUnaryOperator
{
    Plus,
    Minus,
    Not,
    BitwiseNot,
    Join,
    Split,
    CaseSensitiveSplit
}

public enum PowerShellWasmBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Range,
    Format,
    LogicalAnd,
    LogicalOr,
    LogicalXor,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    Join,
    Split,
    CaseSensitiveSplit,
    ShiftLeft,
    ShiftRight,
    NullCoalesce,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Like,
    NotLike,
    Match,
    NotMatch,
    Replace,
    Contains,
    NotContains,
    In,
    NotIn,
    TypeIs,
    TypeIsNot,
    TypeAs,
    CaseSensitiveEqual,
    CaseSensitiveNotEqual,
    CaseSensitiveGreaterThan,
    CaseSensitiveGreaterThanOrEqual,
    CaseSensitiveLessThan,
    CaseSensitiveLessThanOrEqual,
    CaseSensitiveLike,
    CaseSensitiveNotLike,
    CaseSensitiveMatch,
    CaseSensitiveNotMatch,
    CaseSensitiveReplace,
    CaseSensitiveContains,
    CaseSensitiveNotContains,
    CaseSensitiveIn,
    CaseSensitiveNotIn
}
