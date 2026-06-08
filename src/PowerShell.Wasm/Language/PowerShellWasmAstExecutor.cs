using System.Globalization;
using System.Text.RegularExpressions;

namespace PSWasm.Language;

// PowerShell source references:
// - src/System.Management.Automation/engine/CommandProcessor.cs
// - src/System.Management.Automation/engine/CommandProcessorBase.cs
// - src/System.Management.Automation/engine/ParameterBinderController.cs
// - src/System.Management.Automation/engine/SessionState*.cs
// Browser note: this executor keeps only browser-safe command dispatch, state, expression, and pipeline behavior.
internal sealed class PowerShellWasmAstExecutor(
    PowerShellWasmExecutionContext executionContext,
    IReadOnlyDictionary<string, IPowerShellWasmCommand> commands)
{
    public async ValueTask ExecuteAsync(ScriptAst script, CancellationToken cancellationToken)
    {
        foreach (var statement in script.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteStatementAsync(statement, [], cancellationToken);
        }
    }

    private async ValueTask ExecuteStatementAsync(
        StatementAst statement,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        switch (statement)
        {
            case AssignmentStatementAst assignment:
                executionContext.SetVariable(assignment.VariableName, EvaluateExpression(assignment.Value));
                break;
            case ExpressionStatementAst expression:
                executionContext.WriteOutput(EvaluateExpression(expression.Expression));
                break;
            case CommandStatementAst command:
                await ExecuteCommandAsync(command.Command, pipelineInput, cancellationToken);
                break;
            case PipelineStatementAst pipeline:
                await ExecutePipelineAsync(pipeline, cancellationToken);
                break;
        }
    }

    private async ValueTask ExecutePipelineAsync(PipelineStatementAst pipeline, CancellationToken cancellationToken)
    {
        IReadOnlyList<object?> input = [];
        foreach (var element in pipeline.Elements)
        {
            var output = new List<object?>();
            using (executionContext.CaptureOutput(output))
            {
                switch (element)
                {
                    case ExpressionPipelineElementAst expression:
                        executionContext.WriteOutput(EvaluateExpression(expression.Expression));
                        break;
                    case CommandPipelineElementAst command:
                        await ExecuteCommandAsync(command.Command, input, cancellationToken);
                        break;
                }
            }

            input = output;
        }

        foreach (var item in input)
        {
            executionContext.WriteOutput(item);
        }
    }

    private async ValueTask ExecuteCommandAsync(
        CommandAst commandAst,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        if (!commands.TryGetValue(commandAst.Name, out var command))
        {
            throw new InvalidOperationException($"Command '{commandAst.Name}' is not registered in this browser runtime.");
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<object?>();

        foreach (var argument in commandAst.Arguments)
        {
            var value = EvaluateExpression(argument.Value);
            if (argument.IsSplat)
            {
                AddSplat(parameters, value);
                continue;
            }

            arguments.Add(value);
        }

        foreach (var parameter in commandAst.Parameters)
        {
            parameters[parameter.Name] = parameter.Value is null ? true : EvaluateExpression(parameter.Value);
        }

        var context = new PowerShellWasmCommandContext(executionContext, parameters, arguments, pipelineInput);
        await command.InvokeAsync(context, cancellationToken);
    }

    private static void AddSplat(Dictionary<string, object?> parameters, object? value)
    {
        if (value is not Dictionary<string, object?> hashtable)
        {
            throw new InvalidOperationException("Splatting requires a hashtable variable.");
        }

        foreach (var item in hashtable)
        {
            parameters[item.Key] = item.Value;
        }
    }

    private object? EvaluateExpression(ExpressionAst expression) =>
        expression switch
        {
            BareWordExpressionAst bareWord => bareWord.Value,
            NumberExpressionAst number => number.Value,
            StringExpressionAst text => text.IsExpandable ? ExpandString(text.Value) : text.Value,
            VariableExpressionAst variable => variable.IsEnvironment
                ? executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty
                : executionContext.GetVariable(variable.Name),
            AssignmentExpressionAst assignment => EvaluateAssignment(assignment),
            HashtableExpressionAst hashtable => EvaluateHashtable(hashtable),
            ArrayExpressionAst array => array.Items.Select(EvaluateExpression).ToArray(),
            ParenthesizedExpressionAst parenthesized => EvaluateExpression(parenthesized.Expression),
            UnaryExpressionAst unary => EvaluateUnary(unary),
            BinaryExpressionAst binary => EvaluateBinary(binary),
            ComparisonExpressionAst comparison => EvaluateComparison(comparison),
            _ => null
        };

    private object? EvaluateAssignment(AssignmentExpressionAst assignment)
    {
        var value = EvaluateExpression(assignment.Value);
        executionContext.SetVariable(assignment.VariableName, value);
        return value;
    }

    private Dictionary<string, object?> EvaluateHashtable(HashtableExpressionAst hashtable)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in hashtable.Entries)
        {
            result[entry.Key] = EvaluateExpression(entry.Value);
        }

        return result;
    }

    private object EvaluateUnary(UnaryExpressionAst unary)
    {
        var value = ToNumber(EvaluateExpression(unary.Operand));
        return NormalizeNumber(unary.Operator == PowerShellWasmUnaryOperator.Minus ? -value : value);
    }

    private object? EvaluateBinary(BinaryExpressionAst binary)
    {
        var left = EvaluateExpression(binary.Left);
        var right = EvaluateExpression(binary.Right);

        if (binary.Operator == PowerShellWasmBinaryOperator.Add && (left is string || right is string))
        {
            return Convert.ToString(left, CultureInfo.InvariantCulture) + Convert.ToString(right, CultureInfo.InvariantCulture);
        }

        var leftNumber = ToNumber(left);
        var rightNumber = ToNumber(right);
        var result = binary.Operator switch
        {
            PowerShellWasmBinaryOperator.Add => leftNumber + rightNumber,
            PowerShellWasmBinaryOperator.Subtract => leftNumber - rightNumber,
            PowerShellWasmBinaryOperator.Multiply => leftNumber * rightNumber,
            PowerShellWasmBinaryOperator.Divide => leftNumber / rightNumber,
            _ => leftNumber
        };

        return NormalizeNumber(result);
    }

    private bool EvaluateComparison(ComparisonExpressionAst comparison)
    {
        var left = EvaluateExpression(comparison.Left);
        var right = EvaluateExpression(comparison.Right);

        if (TryToNumber(left, out var leftNumber) && TryToNumber(right, out var rightNumber))
        {
            var numericComparison = leftNumber.CompareTo(rightNumber);
            return Compare(numericComparison, comparison.Operator);
        }

        var stringComparison = string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

        return Compare(stringComparison, comparison.Operator);
    }

    private string ExpandString(string value) =>
        Regex.Replace(value, @"\$(env:)?([A-Za-z_][A-Za-z0-9_]*)", match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Success)
            {
                return executionContext.GetEnvironmentVariable(name) ?? string.Empty;
            }

            return executionContext.GetVariable(name)?.ToString() ?? string.Empty;
        });

    private static double ToNumber(object? value) =>
        value switch
        {
            int intValue => intValue,
            double doubleValue => doubleValue,
            decimal decimalValue => (double)decimalValue,
            string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Value '{value}' is not numeric.")
        };

    private static bool TryToNumber(object? value, out double number)
    {
        switch (value)
        {
            case int intValue:
                number = intValue;
                return true;
            case double doubleValue:
                number = doubleValue;
                return true;
            case decimal decimalValue:
                number = (double)decimalValue;
                return true;
            case string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static bool Compare(int comparison, PowerShellWasmComparisonOperator op) =>
        op switch
        {
            PowerShellWasmComparisonOperator.Equal => comparison == 0,
            PowerShellWasmComparisonOperator.NotEqual => comparison != 0,
            PowerShellWasmComparisonOperator.GreaterThan => comparison > 0,
            PowerShellWasmComparisonOperator.GreaterThanOrEqual => comparison >= 0,
            PowerShellWasmComparisonOperator.LessThan => comparison < 0,
            PowerShellWasmComparisonOperator.LessThanOrEqual => comparison <= 0,
            _ => false
        };

    private static object NormalizeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000000001 && value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : value;
}
