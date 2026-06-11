using System.Globalization;
using System.Runtime.ExceptionServices;
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
    private const int MaximumLoopIterations = 10_000;

    public async ValueTask ExecuteAsync(ScriptAst script, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var statement in script.Statements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteStatementWithStatusAsync(statement, [], cancellationToken);
            }
        }
        catch (ReturnFlowException)
        {
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
            case StatementAssignmentAst assignment:
                await ExecuteStatementAssignmentAsync(assignment, cancellationToken);
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
            case PipelineChainStatementAst pipelineChain:
                await ExecutePipelineChainAsync(pipelineChain, cancellationToken);
                break;
            case TryStatementAst tryStatement:
                await ExecuteTryStatementAsync(tryStatement, cancellationToken);
                break;
            case IfStatementAst ifStatement:
                await ExecuteIfStatementAsync(ifStatement, cancellationToken);
                break;
            case ForEachStatementAst forEachStatement:
                await ExecuteForEachStatementAsync(forEachStatement, cancellationToken);
                break;
            case WhileStatementAst whileStatement:
                await ExecuteWhileStatementAsync(whileStatement, cancellationToken);
                break;
            case ForStatementAst forStatement:
                await ExecuteForStatementAsync(forStatement, cancellationToken);
                break;
            case SwitchStatementAst switchStatement:
                await ExecuteSwitchStatementAsync(switchStatement, cancellationToken);
                break;
            case FunctionDefinitionStatementAst functionDefinition:
                executionContext.SetFunction(new PowerShellWasmScriptFunction(
                    functionDefinition.Name,
                    functionDefinition.ParameterNames,
                    functionDefinition.Body));
                break;
            case ReturnStatementAst returnStatement:
                if (returnStatement.Expression is not null)
                {
                    executionContext.WriteOutput(EvaluateExpression(returnStatement.Expression));
                }

                throw new ReturnFlowException();
            case BreakStatementAst:
                throw new LoopBreakFlowException();
            case ContinueStatementAst:
                throw new LoopContinueFlowException();
        }
    }

    private async ValueTask ExecuteIfStatementAsync(IfStatementAst statement, CancellationToken cancellationToken)
    {
        foreach (var clause in statement.Clauses)
        {
            if (ToBoolean(EvaluateExpression(clause.Condition)))
            {
                await ExecuteScriptAsync(clause.Body, cancellationToken);
                return;
            }
        }

        if (statement.ElseBlock is not null)
        {
            await ExecuteScriptAsync(statement.ElseBlock, cancellationToken);
        }
    }

    private async ValueTask ExecuteForEachStatementAsync(ForEachStatementAst statement, CancellationToken cancellationToken)
    {
        foreach (var item in Enumerate(EvaluateExpression(statement.Collection)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            executionContext.SetVariable(statement.VariableName, item);
            try
            {
                await ExecuteScriptAsync(statement.Body, cancellationToken);
            }
            catch (LoopContinueFlowException)
            {
                continue;
            }
            catch (LoopBreakFlowException)
            {
                break;
            }
        }
    }

    private async ValueTask ExecuteWhileStatementAsync(WhileStatementAst statement, CancellationToken cancellationToken)
    {
        var iterations = 0;
        while (ToBoolean(EvaluateExpression(statement.Condition)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > MaximumLoopIterations)
            {
                throw new InvalidOperationException($"The browser-safe while loop exceeded {MaximumLoopIterations} iterations.");
            }

            try
            {
                await ExecuteScriptAsync(statement.Body, cancellationToken);
            }
            catch (LoopContinueFlowException)
            {
                continue;
            }
            catch (LoopBreakFlowException)
            {
                break;
            }
        }
    }

    private async ValueTask ExecuteForStatementAsync(ForStatementAst statement, CancellationToken cancellationToken)
    {
        if (statement.Initializer is not null)
        {
            await ExecuteStatementDiscardingOutputAsync(statement.Initializer, cancellationToken);
        }

        var iterations = 0;
        while (statement.Condition is null || ToBoolean(EvaluateExpression(statement.Condition)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > MaximumLoopIterations)
            {
                throw new InvalidOperationException($"The browser-safe for loop exceeded {MaximumLoopIterations} iterations.");
            }

            try
            {
                await ExecuteScriptAsync(statement.Body, cancellationToken);
            }
            catch (LoopContinueFlowException)
            {
            }
            catch (LoopBreakFlowException)
            {
                break;
            }

            if (statement.Iterator is not null)
            {
                await ExecuteStatementDiscardingOutputAsync(statement.Iterator, cancellationToken);
            }
        }
    }

    private async ValueTask ExecuteSwitchStatementAsync(SwitchStatementAst statement, CancellationToken cancellationToken)
    {
        var breakSwitch = false;
        foreach (var input in Enumerate(EvaluateExpression(statement.Input)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matched = false;
            var continueInput = false;

            foreach (var clause in statement.Clauses)
            {
                if (!SwitchMatches(input, EvaluateExpression(clause.Pattern), statement.UseRegex, statement.CaseSensitive))
                {
                    continue;
                }

                matched = true;
                try
                {
                    await ExecuteScriptAsync(clause.Body, cancellationToken);
                }
                catch (LoopContinueFlowException)
                {
                    continueInput = true;
                    break;
                }
                catch (LoopBreakFlowException)
                {
                    breakSwitch = true;
                    break;
                }
            }

            if (breakSwitch)
            {
                break;
            }

            if (continueInput)
            {
                continue;
            }

            if (!matched)
            {
                foreach (var defaultBlock in statement.DefaultBlocks)
                {
                    try
                    {
                        await ExecuteScriptAsync(defaultBlock, cancellationToken);
                    }
                    catch (LoopContinueFlowException)
                    {
                        continueInput = true;
                        break;
                    }
                    catch (LoopBreakFlowException)
                    {
                        breakSwitch = true;
                        break;
                    }
                }
            }

            if (breakSwitch)
            {
                break;
            }
        }
    }

    private bool SwitchMatches(object? input, object? pattern, bool useRegex, bool caseSensitive) =>
        Enumerate(pattern).Any(patternItem =>
        {
            if (useRegex)
            {
                return RegexMatch(input, patternItem, caseSensitive);
            }

            var patternText = ToInvariantString(patternItem);
            return patternText.Contains('*', StringComparison.Ordinal) || patternText.Contains('?', StringComparison.Ordinal)
                ? WildcardMatch(input, patternItem, caseSensitive)
                : CompareValues(input, patternItem, caseSensitive) == 0;
        });

    private async ValueTask ExecuteStatementAssignmentAsync(StatementAssignmentAst assignment, CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output))
        {
            await ExecuteStatementAsync(assignment.Statement, [], cancellationToken);
        }

        var value = output.Count switch
        {
            0 => null,
            1 => output[0],
            _ => output.ToArray()
        };
        executionContext.SetVariable(assignment.VariableName, value);
    }

    private async ValueTask ExecuteStatementDiscardingOutputAsync(StatementAst statement, CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output))
        {
            await ExecuteStatementAsync(statement, [], cancellationToken);
        }
    }

    private async ValueTask ExecuteTryStatementAsync(TryStatementAst statement, CancellationToken cancellationToken)
    {
        Exception? pending = null;

        try
        {
            await ExecuteScriptAsync(statement.TryBlock, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ControlFlowException flow)
        {
            pending = flow;
        }
        catch (Exception error)
        {
            if (statement.CatchBlocks.Count == 0)
            {
                pending = error;
            }
            else
            {
                try
                {
                    using (executionContext.WithPipelineItem(executionContext.RecordException(error)))
                    {
                        await ExecuteScriptAsync(statement.CatchBlocks[0], cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception catchError)
                {
                    pending = catchError;
                }
            }
        }

        if (statement.FinallyBlock is not null)
        {
            await ExecuteScriptAsync(statement.FinallyBlock, cancellationToken);
        }

        if (pending is not null)
        {
            ExceptionDispatchInfo.Capture(pending).Throw();
        }
    }

    private async ValueTask ExecuteScriptAsync(ScriptAst script, CancellationToken cancellationToken)
    {
        foreach (var statement in script.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteStatementWithStatusAsync(statement, [], cancellationToken);
        }
    }

    private async ValueTask ExecutePipelineChainAsync(PipelineChainStatementAst chain, CancellationToken cancellationToken)
    {
        var succeeded = await ExecuteStatementAndGetSuccessAsync(chain.First, cancellationToken);
        foreach (var clause in chain.Clauses)
        {
            var shouldExecute = clause.Operator == PipelineChainOperator.And ? succeeded : !succeeded;
            if (shouldExecute)
            {
                succeeded = await ExecuteStatementAndGetSuccessAsync(clause.Statement, cancellationToken);
            }
        }
    }

    private async ValueTask<bool> ExecuteStatementAndGetSuccessAsync(StatementAst statement, CancellationToken cancellationToken)
    {
        var errorCount = executionContext.ErrorCount;
        await ExecuteStatementWithStatusAsync(statement, [], cancellationToken);
        return executionContext.ErrorCount == errorCount;
    }

    private async ValueTask ExecuteStatementWithStatusAsync(
        StatementAst statement,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        var errorCount = executionContext.ErrorCount;
        var failureSignalCount = executionContext.FailureSignalCount;
        try
        {
            await ExecuteStatementAsync(statement, pipelineInput, cancellationToken);
            executionContext.SetLastCommandSucceeded(
                executionContext.ErrorCount == errorCount &&
                executionContext.FailureSignalCount == failureSignalCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ControlFlowException)
        {
            throw;
        }
        catch (Exception error)
        {
            executionContext.RecordException(error);
            executionContext.SetLastCommandSucceeded(false);
            throw;
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
                        foreach (var item in Enumerate(EvaluateExpression(expression.Expression)))
                        {
                            executionContext.WriteOutput(item);
                        }

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
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<object?>();

        foreach (var argument in commandAst.Arguments)
        {
            var value = EvaluateExpression(argument.Value);
            if (argument.IsSplat)
            {
                AddSplat(parameters, arguments, value);
                continue;
            }

            arguments.Add(value);
        }

        foreach (var parameter in commandAst.Parameters)
        {
            parameters[parameter.Name] = parameter.Value is null ? true : EvaluateExpression(parameter.Value);
        }

        var commonParameters = PowerShellWasmCommonParameters.From(parameters);
        if (executionContext.TryGetFunction(commandAst.Name, out var function))
        {
            await ExecuteWithCommonParametersAsync(
                commonParameters,
                () => ExecuteScriptFunctionAsync(function, parameters, arguments, pipelineInput, cancellationToken));
            return;
        }

        if (!commands.TryGetValue(commandAst.Name, out var command))
        {
            throw new InvalidOperationException($"Command '{commandAst.Name}' is not registered in this browser runtime.");
        }

        var context = new PowerShellWasmCommandContext(executionContext, parameters, arguments, pipelineInput);
        await ExecuteWithCommonParametersAsync(
            commonParameters,
            () => command.InvokeAsync(context, cancellationToken));
    }

    private async ValueTask ExecuteWithCommonParametersAsync(
        PowerShellWasmCommonParameters commonParameters,
        Func<ValueTask> invoke)
    {
        var capturedOutput = new List<object?>();
        var initialErrorCount = executionContext.ErrorCount;
        ExceptionDispatchInfo? pending = null;

        using (executionContext.CaptureOutput(capturedOutput))
        using (commonParameters.Apply(executionContext))
        {
            try
            {
                await invoke();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                pending = ExceptionDispatchInfo.Capture(error);
            }
        }

        commonParameters.ApplyCaptures(executionContext, capturedOutput, initialErrorCount);
        executionContext.WriteCapturedOutput(capturedOutput);
        pending?.Throw();
    }

    private async ValueTask ExecuteScriptFunctionAsync(
        PowerShellWasmScriptFunction function,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        var locals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = pipelineInput.ToArray()
        };
        var boundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var argumentIndex = 0;

        foreach (var parameterName in function.ParameterNames)
        {
            if (parameters.TryGetValue(parameterName, out var parameterValue))
            {
                locals[parameterName] = parameterValue;
                boundParameters[parameterName] = parameterValue;
                continue;
            }

            if (argumentIndex < arguments.Count)
            {
                var argumentValue = arguments[argumentIndex++];
                locals[parameterName] = argumentValue;
                boundParameters[parameterName] = argumentValue;
            }
            else
            {
                locals[parameterName] = null;
            }
        }

        locals["PSBoundParameters"] = boundParameters;
        locals["args"] = function.ParameterNames.Count == 0
            ? arguments.ToArray()
            : arguments.Skip(argumentIndex).ToArray();

        using (executionContext.WithVariableScope(locals))
        {
            try
            {
                await ExecuteScriptAsync(function.Body, cancellationToken);
            }
            catch (ReturnFlowException)
            {
            }
        }
    }

    private static void AddSplat(Dictionary<string, object?> parameters, List<object?> arguments, object? value)
    {
        if (TryAsDictionary(value, out var hashtable))
        {
            foreach (var item in hashtable)
            {
                parameters[item.Key] = item.Value;
            }

            return;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                arguments.Add(item);
            }

            return;
        }

        throw new InvalidOperationException("Splatting requires a hashtable or array variable.");
    }

    private object? EvaluateExpression(ExpressionAst expression) =>
        expression switch
        {
            BareWordExpressionAst bareWord => bareWord.Value,
            NumberExpressionAst number => number.Value,
            StringExpressionAst text => text.IsExpandable ? ExpandString(text.Value) : text.Value,
            VariableExpressionAst variable => variable.IsEnvironment
                ? executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty
                : EvaluateVariable(variable.Name),
            AssignmentExpressionAst assignment => EvaluateAssignment(assignment),
            HashtableExpressionAst hashtable => EvaluateHashtable(hashtable),
            ArrayExpressionAst array => array.Items.Select(EvaluateExpression).ToArray(),
            ScriptBlockExpressionAst scriptBlock => CreateScriptBlock(scriptBlock),
            ParenthesizedExpressionAst parenthesized => EvaluateExpression(parenthesized.Expression),
            MemberAccessExpressionAst member => EvaluateMemberAccess(member),
            IndexExpressionAst index => EvaluateIndex(index),
            UnaryExpressionAst unary => EvaluateUnary(unary),
            BinaryExpressionAst binary => EvaluateBinary(binary),
            _ => null
        };

    private object? EvaluateVariable(string name) =>
        name.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => executionContext.GetVariable(name)
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

    private PowerShellWasmScriptBlock CreateScriptBlock(ScriptBlockExpressionAst scriptBlock) =>
        new(async (input, cancellationToken) =>
        {
            var output = new List<object?>();
            using (executionContext.WithPipelineItem(input))
            using (executionContext.CaptureOutput(output))
            {
                try
                {
                    foreach (var statement in scriptBlock.Body.Statements)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ExecuteStatementAsync(statement, [], cancellationToken);
                    }
                }
                catch (ReturnFlowException)
                {
                }
            }

            return output;
        });

    private object? EvaluateMemberAccess(MemberAccessExpressionAst member)
    {
        var target = EvaluateExpression(member.Target);
        if (target is null)
        {
            return null;
        }

        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            readOnlyDictionary.TryGetValue(member.MemberName, out var readOnlyValue))
        {
            return readOnlyValue;
        }

        if (target is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue(member.MemberName, out var value))
        {
            return value;
        }

        if (target is System.Collections.IDictionary legacyDictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
            {
                if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), member.MemberName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
        }

        if (TryGetCollectionProperty(target, member.MemberName, out var collectionValue))
        {
            return collectionValue;
        }

        return null;
    }

    private object? EvaluateIndex(IndexExpressionAst index)
    {
        var target = EvaluateExpression(index.Target);
        if (TryGetDictionaryIndex(target, EvaluateExpression(index.Index), out var dictionaryValue))
        {
            return dictionaryValue;
        }

        var values = ToIndexableValues(target);
        if (values.Length == 0)
        {
            return null;
        }

        var selected = new List<object?>();
        foreach (var indexValue in Enumerate(EvaluateExpression(index.Index)))
        {
            var itemIndex = Convert.ToInt32(ToNumber(indexValue), CultureInfo.InvariantCulture);
            if (itemIndex < 0)
            {
                itemIndex = values.Length + itemIndex;
            }

            if (itemIndex >= 0 && itemIndex < values.Length)
            {
                selected.Add(values[itemIndex]);
            }
        }

        return selected.Count switch
        {
            0 => null,
            1 => selected[0],
            _ => selected.ToArray()
        };
    }

    private static bool TryGetDictionaryIndex(object? target, object? index, out object? value)
    {
        value = null;
        if (!TryAsDictionary(target, out var dictionary))
        {
            return false;
        }

        var selected = new List<object?>();
        foreach (var keyValue in Enumerate(index))
        {
            var key = ToInvariantString(keyValue);
            if (TryGetDictionaryValue(dictionary, key, out var item))
            {
                selected.Add(item);
            }
        }

        value = selected.Count switch
        {
            0 => null,
            1 => selected[0],
            _ => selected.ToArray()
        };
        return true;
    }

    private object EvaluateUnary(UnaryExpressionAst unary)
    {
        var value = EvaluateExpression(unary.Operand);
        return unary.Operator switch
        {
            PowerShellWasmUnaryOperator.Plus => NormalizeNumber(ToNumber(value)),
            PowerShellWasmUnaryOperator.Minus => NormalizeNumber(-ToNumber(value)),
            PowerShellWasmUnaryOperator.Not => !ToBoolean(value),
            PowerShellWasmUnaryOperator.BitwiseNot => ~ToInt64(value),
            PowerShellWasmUnaryOperator.Join => string.Concat(Enumerate(value).Select(ToInvariantString)),
            PowerShellWasmUnaryOperator.Split => SplitString(ToInvariantString(value), @"\s+", ignoreCase: true),
            PowerShellWasmUnaryOperator.CaseSensitiveSplit => SplitString(ToInvariantString(value), @"\s+", ignoreCase: false),
            _ => value ?? string.Empty
        };
    }

    private object? EvaluateBinary(BinaryExpressionAst binary)
    {
        var left = EvaluateExpression(binary.Left);
        if (binary.Operator == PowerShellWasmBinaryOperator.NullCoalesce)
        {
            return left ?? EvaluateExpression(binary.Right);
        }

        if (binary.Operator == PowerShellWasmBinaryOperator.LogicalAnd)
        {
            return ToBoolean(left) && ToBoolean(EvaluateExpression(binary.Right));
        }

        if (binary.Operator == PowerShellWasmBinaryOperator.LogicalOr)
        {
            return ToBoolean(left) || ToBoolean(EvaluateExpression(binary.Right));
        }

        var right = EvaluateExpression(binary.Right);
        return binary.Operator switch
        {
            PowerShellWasmBinaryOperator.Add => Add(left, right),
            PowerShellWasmBinaryOperator.Subtract => NormalizeNumber(ToNumber(left) - ToNumber(right)),
            PowerShellWasmBinaryOperator.Multiply => NormalizeNumber(ToNumber(left) * ToNumber(right)),
            PowerShellWasmBinaryOperator.Divide => NormalizeNumber(ToNumber(left) / ToNumber(right)),
            PowerShellWasmBinaryOperator.Remainder => NormalizeNumber(ToNumber(left) % ToNumber(right)),
            PowerShellWasmBinaryOperator.Range => Range(left, right),
            PowerShellWasmBinaryOperator.Format => Format(left, right),
            PowerShellWasmBinaryOperator.LogicalXor => ToBoolean(left) ^ ToBoolean(right),
            PowerShellWasmBinaryOperator.BitwiseAnd => ToInt64(left) & ToInt64(right),
            PowerShellWasmBinaryOperator.BitwiseOr => ToInt64(left) | ToInt64(right),
            PowerShellWasmBinaryOperator.BitwiseXor => ToInt64(left) ^ ToInt64(right),
            PowerShellWasmBinaryOperator.Join => Join(left, right),
            PowerShellWasmBinaryOperator.Split => SplitString(ToInvariantString(left), ToInvariantString(right), ignoreCase: true),
            PowerShellWasmBinaryOperator.CaseSensitiveSplit => SplitString(ToInvariantString(left), ToInvariantString(right), ignoreCase: false),
            PowerShellWasmBinaryOperator.ShiftLeft => ToInt64(left) << Convert.ToInt32(ToInt64(right), CultureInfo.InvariantCulture),
            PowerShellWasmBinaryOperator.ShiftRight => ToInt64(left) >> Convert.ToInt32(ToInt64(right), CultureInfo.InvariantCulture),
            PowerShellWasmBinaryOperator.Equal => CompareValues(left, right, caseSensitive: false) == 0,
            PowerShellWasmBinaryOperator.NotEqual => CompareValues(left, right, caseSensitive: false) != 0,
            PowerShellWasmBinaryOperator.GreaterThan => CompareValues(left, right, caseSensitive: false) > 0,
            PowerShellWasmBinaryOperator.GreaterThanOrEqual => CompareValues(left, right, caseSensitive: false) >= 0,
            PowerShellWasmBinaryOperator.LessThan => CompareValues(left, right, caseSensitive: false) < 0,
            PowerShellWasmBinaryOperator.LessThanOrEqual => CompareValues(left, right, caseSensitive: false) <= 0,
            PowerShellWasmBinaryOperator.Like => WildcardMatch(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotLike => !WildcardMatch(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.Match => RegexMatch(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotMatch => !RegexMatch(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.Replace => ReplaceString(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.Contains => Contains(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotContains => !Contains(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.In => Contains(right, left, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotIn => !Contains(right, left, caseSensitive: false),
            PowerShellWasmBinaryOperator.CaseSensitiveEqual => CompareValues(left, right, caseSensitive: true) == 0,
            PowerShellWasmBinaryOperator.CaseSensitiveNotEqual => CompareValues(left, right, caseSensitive: true) != 0,
            PowerShellWasmBinaryOperator.CaseSensitiveGreaterThan => CompareValues(left, right, caseSensitive: true) > 0,
            PowerShellWasmBinaryOperator.CaseSensitiveGreaterThanOrEqual => CompareValues(left, right, caseSensitive: true) >= 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLessThan => CompareValues(left, right, caseSensitive: true) < 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLessThanOrEqual => CompareValues(left, right, caseSensitive: true) <= 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLike => WildcardMatch(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotLike => !WildcardMatch(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveMatch => RegexMatch(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotMatch => !RegexMatch(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveReplace => ReplaceString(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveContains => Contains(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotContains => !Contains(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveIn => Contains(right, left, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotIn => !Contains(right, left, caseSensitive: true),
            _ => left
        };
    }

    private static object Add(object? left, object? right)
    {
        if (left is string || right is string)
        {
            return ToInvariantString(left) + ToInvariantString(right);
        }

        if (left is object?[] leftArray)
        {
            return leftArray.Concat(Enumerate(right)).ToArray();
        }

        return NormalizeNumber(ToNumber(left) + ToNumber(right));
    }

    private static object?[] Range(object? left, object? right)
    {
        var start = Convert.ToInt32(ToNumber(left), CultureInfo.InvariantCulture);
        var end = Convert.ToInt32(ToNumber(right), CultureInfo.InvariantCulture);
        var count = Math.Abs(end - start) + 1;
        var step = start <= end ? 1 : -1;
        var result = new object?[count];

        for (var i = 0; i < count; i++)
        {
            result[i] = start + (i * step);
        }

        return result;
    }

    private static string Format(object? left, object? right)
    {
        var args = Enumerate(right).ToArray();
        return string.Format(CultureInfo.InvariantCulture, ToInvariantString(left), args);
    }

    private static string Join(object? left, object? right) =>
        string.Join(ToInvariantString(right), Enumerate(left).Select(ToInvariantString));

    private static int CompareValues(object? left, object? right, bool caseSensitive)
    {
        if (TryToNumber(left, out var leftNumber) && TryToNumber(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.Compare(ToInvariantString(left), ToInvariantString(right),
            caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatch(object? left, object? right, bool caseSensitive)
    {
        var pattern = "^" + Regex.Escape(ToInvariantString(right)).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(ToInvariantString(left), pattern, RegexOptionsFor(caseSensitive));
    }

    private bool RegexMatch(object? left, object? right, bool caseSensitive)
    {
        var regex = new Regex(ToInvariantString(right), RegexOptionsFor(caseSensitive));
        var match = regex.Match(ToInvariantString(left));
        if (match.Success)
        {
            var matches = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = match.Value
            };

            for (var i = 1; i < match.Groups.Count; i++)
            {
                if (match.Groups[i].Success)
                {
                    matches[i.ToString(CultureInfo.InvariantCulture)] = match.Groups[i].Value;
                }
            }

            foreach (var name in regex.GetGroupNames().Where(static name => !int.TryParse(name, out _)))
            {
                if (match.Groups[name].Success)
                {
                    matches[name] = match.Groups[name].Value;
                }
            }

            executionContext.SetVariable("Matches", matches);
        }

        return match.Success;
    }

    private static string ReplaceString(object? left, object? right, bool caseSensitive)
    {
        var args = Enumerate(right).Select(ToInvariantString).ToArray();
        var pattern = args.Length > 0 ? args[0] : string.Empty;
        var replacement = args.Length > 1 ? args[1] : string.Empty;
        return Regex.Replace(ToInvariantString(left), pattern, replacement, RegexOptionsFor(caseSensitive));
    }

    private static object?[] SplitString(string value, string pattern, bool ignoreCase)
    {
        var options = ignoreCase ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase : RegexOptions.CultureInvariant;
        return Regex.Split(value, pattern, options).Where(static item => item.Length > 0).Cast<object?>().ToArray();
    }

    private static bool Contains(object? collection, object? value, bool caseSensitive) =>
        Enumerate(collection).Any(item => CompareValues(item, value, caseSensitive) == 0);

    private static RegexOptions RegexOptionsFor(bool caseSensitive) =>
        caseSensitive ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IDictionary or IReadOnlyDictionary<string, object?>)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private static object?[] ToIndexableValues(object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (value is string text)
        {
            return text.Select(static ch => ch.ToString()).Cast<object?>().ToArray();
        }

        if (value is System.Collections.IDictionary or IReadOnlyDictionary<string, object?>)
        {
            return [value];
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().ToArray();
        }

        return [value];
    }

    private static bool TryGetCollectionProperty(object? target, string memberName, out object? value)
    {
        value = null;
        if (TryAsDictionary(target, out var dictionary))
        {
            if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
                memberName.Equals("LongLength", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Count;
                return true;
            }

            if (memberName.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Keys.Cast<object?>().ToArray();
                return true;
            }

            if (memberName.Equals("Values", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Values.ToArray();
                return true;
            }
        }

        if (memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) && target is string text)
        {
            value = text.Length;
            return true;
        }

        if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) && target is string)
        {
            value = 1;
            return true;
        }

        if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
            memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
            memberName.Equals("LongLength", StringComparison.OrdinalIgnoreCase))
        {
            value = ToIndexableValues(target).Length;
            return true;
        }

        if (memberName.Equals("Rank", StringComparison.OrdinalIgnoreCase))
        {
            value = target is System.Collections.IEnumerable and not string ? 1 : 0;
            return true;
        }

        return false;
    }

    private static bool TryAsDictionary(object? target, out Dictionary<string, object?> dictionary)
    {
        dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (target)
        {
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                foreach (var item in readOnlyDictionary)
                {
                    dictionary[item.Key] = item.Value;
                }

                return true;
            case IDictionary<string, object?> genericDictionary:
                foreach (var item in genericDictionary)
                {
                    dictionary[item.Key] = item.Value;
                }

                return true;
            case System.Collections.IDictionary legacyDictionary:
                foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
                {
                    dictionary[ToInvariantString(entry.Key)] = entry.Value;
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDictionaryValue(Dictionary<string, object?> dictionary, string key, out object? value) =>
        dictionary.TryGetValue(key, out value);

    private string ExpandString(string value) =>
        Regex.Replace(value, @"\$(env:)?([A-Za-z_][A-Za-z0-9_]*)", match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Success)
            {
                return executionContext.GetEnvironmentVariable(name) ?? string.Empty;
            }

            return ToExpandableString(executionContext.GetVariable(name));
        });

    private string ToExpandableString(object? value) =>
        value is object?[] array
            ? string.Join(executionContext.OutputFieldSeparator, array.Select(ToInvariantString))
            : ToInvariantString(value);

    private static double ToNumber(object? value) =>
        value switch
        {
            bool boolValue => boolValue ? 1 : 0,
            int intValue => intValue,
            long longValue => longValue,
            double doubleValue => doubleValue,
            decimal decimalValue => (double)decimalValue,
            string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Value '{value}' is not numeric.")
        };

    private static long ToInt64(object? value) =>
        Convert.ToInt64(ToNumber(value), CultureInfo.InvariantCulture);

    private static bool ToBoolean(object? value) =>
        value switch
        {
            null => false,
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            double doubleValue => Math.Abs(doubleValue) > 0.0000000001,
            decimal decimalValue => decimalValue != 0,
            string stringValue => stringValue.Length > 0,
            object?[] arrayValue => arrayValue.Length > 0,
            _ => true
        };

    private static bool TryToNumber(object? value, out double number)
    {
        switch (value)
        {
            case bool boolValue:
                number = boolValue ? 1 : 0;
                return true;
            case int intValue:
                number = intValue;
                return true;
            case long longValue:
                number = longValue;
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

    private static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static object NormalizeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000000001 && value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : value;

    private abstract class ControlFlowException : Exception
    {
    }

    private sealed class ReturnFlowException : ControlFlowException
    {
    }

    private sealed class LoopBreakFlowException : ControlFlowException
    {
    }

    private sealed class LoopContinueFlowException : ControlFlowException
    {
    }
}
