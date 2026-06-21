using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
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
            await ExecuteScriptAsync(script, cancellationToken);
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
                executionContext.SetVariable(assignment.VariableName, await EvaluateExpressionAsync(assignment.Value, cancellationToken));
                break;
            case CompoundAssignmentStatementAst assignment:
                await EvaluateCompoundAssignmentAsync(assignment.VariableName, assignment.Operator, assignment.Value, cancellationToken);
                break;
            case ParallelAssignmentStatementAst assignment:
                AssignParallel(assignment.VariableNames, await EvaluateExpressionAsync(assignment.Value, cancellationToken));
                break;
            case VariableIncrementStatementAst increment:
                IncrementVariable(increment.VariableName, increment.Delta);
                break;
            case StatementAssignmentAst assignment:
                await ExecuteStatementAssignmentAsync(assignment, cancellationToken);
                break;
            case ParallelStatementAssignmentAst assignment:
                await ExecuteParallelStatementAssignmentAsync(assignment, cancellationToken);
                break;
            case ExpressionStatementAst expression:
                executionContext.WriteOutput(await EvaluateExpressionAsync(expression.Expression, cancellationToken));
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
                    functionDefinition.Parameters,
                    functionDefinition.Body));
                break;
            case ParamBlockStatementAst:
                throw new InvalidOperationException("A param block must be the first statement in a script or script block.");
            case MetadataAttributeStatementAst:
                break;
            case ReturnStatementAst returnStatement:
                if (returnStatement.Expression is not null)
                {
                    executionContext.WriteOutput(await EvaluateExpressionAsync(returnStatement.Expression, cancellationToken));
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
            if (ToBoolean(await EvaluateExpressionAsync(clause.Condition, cancellationToken)))
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
        foreach (var item in Enumerate(await EvaluateExpressionAsync(statement.Collection, cancellationToken)))
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
        while (ToBoolean(await EvaluateExpressionAsync(statement.Condition, cancellationToken)))
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
        while (statement.Condition is null || ToBoolean(await EvaluateExpressionAsync(statement.Condition, cancellationToken)))
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
        foreach (var input in Enumerate(await EvaluateExpressionAsync(statement.Input, cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matched = false;
            var continueInput = false;

            foreach (var clause in statement.Clauses)
            {
                if (!SwitchMatches(
                    input,
                    await EvaluateExpressionAsync(clause.Pattern, cancellationToken),
                    statement.UseRegex,
                    statement.CaseSensitive))
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

    private async ValueTask ExecuteParallelStatementAssignmentAsync(
        ParallelStatementAssignmentAst assignment,
        CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output))
        {
            await ExecuteStatementAsync(assignment.Statement, [], cancellationToken);
        }

        AssignParallel(assignment.VariableNames, output.ToArray());
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
        using var parameterScope = script.Parameters.Count == 0
            ? null
            : executionContext.WithTemporaryVariables(
                await CreateParameterLocalsAsync(script.Parameters, null, [], null, bindInputToFirstParameter: false, null, cancellationToken));

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
                        foreach (var item in Enumerate(await EvaluateExpressionAsync(expression.Expression, cancellationToken)))
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
            var value = await EvaluateExpressionAsync(argument.Value, cancellationToken);
            if (argument.IsSplat)
            {
                AddSplat(parameters, arguments, value);
                continue;
            }

            arguments.Add(value);
        }

        foreach (var parameter in commandAst.Parameters)
        {
            parameters[parameter.Name] = parameter.Value is null
                ? true
                : await EvaluateExpressionAsync(parameter.Value, cancellationToken);
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
        var locals = await CreateParameterLocalsAsync(
            function.Parameters,
            parameters,
            arguments,
            null,
            bindInputToFirstParameter: false,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["input"] = pipelineInput.ToArray()
            },
            cancellationToken);

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

    private async ValueTask<object?> EvaluateExpressionAsync(ExpressionAst expression, CancellationToken cancellationToken)
    {
        switch (expression)
        {
            case BareWordExpressionAst bareWord:
                return bareWord.Value;
            case NumberExpressionAst number:
                return number.Value;
            case StringExpressionAst text:
                return text.IsExpandable ? await ExpandStringAsync(text.Value, cancellationToken) : text.Value;
            case VariableExpressionAst variable:
                return variable.IsEnvironment
                    ? executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty
                    : EvaluateVariable(variable.Name);
            case AssignmentExpressionAst assignment:
                return await EvaluateAssignmentAsync(assignment, cancellationToken);
            case CompoundAssignmentExpressionAst assignment:
                return await EvaluateCompoundAssignmentAsync(
                    assignment.VariableName,
                    assignment.Operator,
                    assignment.Value,
                    cancellationToken);
            case HashtableExpressionAst hashtable:
                return await EvaluateHashtableAsync(hashtable, cancellationToken);
            case TypedHashtableExpressionAst typedHashtable:
                return await EvaluateTypedHashtableAsync(typedHashtable, cancellationToken);
            case ArrayExpressionAst array:
                return await EvaluateArrayAsync(array, cancellationToken);
            case ScriptBlockExpressionAst scriptBlock:
                return CreateScriptBlock(scriptBlock);
            case ParenthesizedExpressionAst parenthesized:
                return await EvaluateExpressionAsync(parenthesized.Expression, cancellationToken);
            case StatementExpressionAst statement:
                return await EvaluateStatementExpressionAsync(statement.Statement, cancellationToken);
            case TypeLiteralExpressionAst typeLiteral:
                return PowerShellWasmDotNetBridge.ResolveType(typeLiteral.TypeName);
            case CastExpressionAst cast:
                return await EvaluateCastAsync(cast, cancellationToken);
            case MemberAccessExpressionAst member:
                return await EvaluateMemberAccessAsync(member, cancellationToken);
            case StaticMemberAccessExpressionAst member:
                return await EvaluateStaticMemberAccessAsync(member, cancellationToken);
            case MethodInvocationExpressionAst invocation:
                return await EvaluateMethodInvocationAsync(invocation, cancellationToken);
            case IndexExpressionAst index:
                return await EvaluateIndexAsync(index, cancellationToken);
            case UnaryExpressionAst unary:
                return await EvaluateUnaryAsync(unary, cancellationToken);
            case BinaryExpressionAst binary:
                return await EvaluateBinaryAsync(binary, cancellationToken);
            default:
                return null;
        }
    }

    private object? EvaluateVariable(string name) =>
        name.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => executionContext.GetVariable(name)
        };

    private async ValueTask<object?> EvaluateAssignmentAsync(AssignmentExpressionAst assignment, CancellationToken cancellationToken)
    {
        var value = await EvaluateExpressionAsync(assignment.Value, cancellationToken);
        executionContext.SetVariable(assignment.VariableName, value);
        return value;
    }

    private async ValueTask<object?> EvaluateCompoundAssignmentAsync(
        string variableName,
        PowerShellWasmBinaryOperator op,
        ExpressionAst expression,
        CancellationToken cancellationToken)
    {
        var left = EvaluateVariable(variableName);
        var right = await EvaluateExpressionAsync(expression, cancellationToken);
        var value = ApplyBinaryOperator(left, op, right);
        executionContext.SetVariable(variableName, value);
        return value;
    }

    private async ValueTask<PowerShellWasmHashtable> EvaluateHashtableAsync(
        HashtableExpressionAst hashtable,
        CancellationToken cancellationToken)
    {
        var result = new PowerShellWasmHashtable();
        foreach (var entry in hashtable.Entries)
        {
            var key = ToInvariantString(await EvaluateExpressionAsync(entry.Key, cancellationToken));
            result[key] = await EvaluateExpressionAsync(entry.Value, cancellationToken);
        }

        return result;
    }

    private async ValueTask<PowerShellWasmHashtable> EvaluateTypedHashtableAsync(
        TypedHashtableExpressionAst typedHashtable,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedTypedHashtable(typedHashtable.TypeName))
        {
            throw new InvalidOperationException(
                $"Typed hashtable literal [{typedHashtable.TypeName}]@{{}} is not available in this browser-safe runtime.");
        }

        return await EvaluateHashtableAsync(typedHashtable.Hashtable, cancellationToken);
    }

    private static bool IsSupportedTypedHashtable(string typeName)
    {
        var normalized = typeName.Trim();
        return normalized.Equals("ordered", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("pscustomobject", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("psobject", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("System.Management.Automation.PSCustomObject", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("System.Management.Automation.PSObject", StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask<object?[]> EvaluateArrayAsync(ArrayExpressionAst array, CancellationToken cancellationToken)
    {
        var result = new object?[array.Items.Count];
        for (var i = 0; i < array.Items.Count; i++)
        {
            result[i] = await EvaluateExpressionAsync(array.Items[i], cancellationToken);
        }

        return result;
    }

    private async ValueTask<object?> EvaluateStatementExpressionAsync(
        StatementAst statement,
        CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output))
        {
            await ExecuteStatementAsync(statement, [], cancellationToken);
        }

        return output.Count switch
        {
            0 => null,
            1 => output[0],
            _ => output.ToArray()
        };
    }

    private PowerShellWasmScriptBlock CreateScriptBlock(ScriptBlockExpressionAst scriptBlock)
    {
        async ValueTask<IReadOnlyList<object?>> InvokeAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            CancellationToken cancellationToken)
        {
            var output = new List<object?>();
            var locals = await CreateParameterLocalsAsync(
                scriptBlock.Body.Parameters,
                null,
                arguments ?? [],
                input,
                bindInputToFirstParameter: arguments is null,
                variables,
                cancellationToken);
            using var variableScope = locals.Count == 0 ? null : executionContext.WithTemporaryVariables(locals);
            using var pipelineScope = executionContext.WithPipelineItem(input);
            using var outputScope = executionContext.CaptureOutput(output);

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

            return output;
        }

        async ValueTask<PowerShellWasmResult> InvokeResultAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            CancellationToken cancellationToken) =>
            executionContext.CreateResult(await InvokeAsync(input, arguments, variables, cancellationToken));

        return new PowerShellWasmScriptBlock(InvokeAsync, InvokeResultAsync);
    }

    private async ValueTask<Dictionary<string, object?>> CreateParameterLocalsAsync(
        IReadOnlyList<ParameterDeclarationAst> parameters,
        IReadOnlyDictionary<string, object?>? namedParameters,
        IReadOnlyList<object?> arguments,
        object? input,
        bool bindInputToFirstParameter,
        IReadOnlyDictionary<string, object?>? variables,
        CancellationToken cancellationToken)
    {
        var locals = variables is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
        var boundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var argumentIndex = 0;
        var inputBound = false;

        foreach (var parameter in parameters)
        {
            var parameterName = parameter.Name;
            object? value;
            var wasBound = false;
            var hasValue = false;
            if (TryGetNamedParameterValue(namedParameters, parameter, out var namedValue))
            {
                value = namedValue;
                wasBound = true;
                hasValue = true;
            }
            else if (argumentIndex < arguments.Count)
            {
                value = arguments[argumentIndex++];
                wasBound = true;
                hasValue = true;
            }
            else if (bindInputToFirstParameter && !inputBound)
            {
                value = input;
                inputBound = true;
                wasBound = true;
                hasValue = true;
            }
            else if (parameter.DefaultValue is not null)
            {
                value = await EvaluateParameterDefaultAsync(parameter.DefaultValue, locals, cancellationToken);
                hasValue = true;
            }
            else
            {
                value = null;
            }

            value = ConvertParameterValue(parameter, value, hasValue);
            ValidateParameterValue(parameter, value);
            locals[parameterName] = value;
            if (wasBound)
            {
                boundParameters[parameterName] = value;
            }
        }

        locals["PSBoundParameters"] = boundParameters;
        locals["args"] = parameters.Count == 0 ? arguments.ToArray() : arguments.Skip(argumentIndex).ToArray();
        return locals;
    }

    private static bool TryGetNamedParameterValue(
        IReadOnlyDictionary<string, object?>? namedParameters,
        ParameterDeclarationAst parameter,
        out object? value)
    {
        value = null;
        if (namedParameters is null)
        {
            return false;
        }

        if (namedParameters.TryGetValue(parameter.Name, out value))
        {
            return true;
        }

        foreach (var alias in parameter.Aliases)
        {
            if (namedParameters.TryGetValue(alias, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static object? ConvertParameterValue(ParameterDeclarationAst parameter, object? value, bool hasValue)
    {
        if (string.IsNullOrWhiteSpace(parameter.TypeName))
        {
            return value;
        }

        if (NormalizeCastTypeName(parameter.TypeName).Equals("switch", StringComparison.Ordinal))
        {
            return hasValue && ToBoolean(value);
        }

        return hasValue ? CastValue(parameter.TypeName, value) : value;
    }

    private static void ValidateParameterValue(ParameterDeclarationAst parameter, object? value)
    {
        if (parameter.ValidateSet.Count == 0)
        {
            return;
        }

        foreach (var item in Enumerate(value))
        {
            var text = ToInvariantString(item);
            if (parameter.ValidateSet.Any(candidate => candidate.Equals(text, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Parameter '{parameter.Name}' failed ValidateSet. Expected one of: {string.Join(", ", parameter.ValidateSet)}.");
        }
    }

    private async ValueTask<object?> EvaluateParameterDefaultAsync(
        ExpressionAst expression,
        IReadOnlyDictionary<string, object?> locals,
        CancellationToken cancellationToken)
    {
        using var parameterScope = locals.Count == 0 ? null : executionContext.WithTemporaryVariables(locals);
        return await EvaluateExpressionAsync(expression, cancellationToken);
    }

    private async ValueTask<object?> EvaluateCastAsync(CastExpressionAst cast, CancellationToken cancellationToken)
    {
        var value = await EvaluateExpressionAsync(cast.Operand, cancellationToken);
        return CastValue(cast.TypeName, value);
    }

    private static object? CastValue(string typeName, object? value)
    {
        var normalizedType = NormalizeCastTypeName(typeName);
        if (normalizedType.EndsWith("[]", StringComparison.Ordinal))
        {
            return CastArrayValue(normalizedType[..^2], value);
        }

        return CastScalarValue(normalizedType, value);
    }

    private static object? CastArrayValue(string elementType, object? value)
    {
        var values = Enumerate(value).Select(item => CastScalarValue(elementType, item)).ToArray();
        return elementType switch
        {
            "byte" => values.Select(item => Convert.ToByte(item, CultureInfo.InvariantCulture)).ToArray(),
            "string" => values.Select(ToInvariantString).ToArray(),
            "int" => values.Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray(),
            "long" => values.Select(item => Convert.ToInt64(item, CultureInfo.InvariantCulture)).ToArray(),
            "double" => values.Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToArray(),
            "bool" => values.Select(ToBoolean).ToArray(),
            "switch" => values.Select(ToBoolean).ToArray(),
            "object" => values,
            _ => throw new InvalidOperationException($"Cast type '[{elementType}[]]' is not available in this browser-safe runtime.")
        };
    }

    private static object? CastScalarValue(string typeName, object? value) =>
        typeName switch
        {
            "object" => value,
            "string" => ToInvariantString(value),
            "bool" => ToBoolean(value),
            "int" => Convert.ToInt32(ToCastNumber(value), CultureInfo.InvariantCulture),
            "long" => Convert.ToInt64(ToCastNumber(value), CultureInfo.InvariantCulture),
            "double" => ToCastNumber(value),
            "decimal" => value is string text
                ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : Convert.ToDecimal(ToCastNumber(value), CultureInfo.InvariantCulture),
            "byte" => Convert.ToByte(ToCastNumber(value), CultureInfo.InvariantCulture),
            "switch" => ToBoolean(value),
            _ => throw new InvalidOperationException($"Cast type '[{typeName}]' is not available in this browser-safe runtime.")
        };

    private static double ToCastNumber(object? value) =>
        value is null ? 0 : ToNumber(value);

    private static string NormalizeCastTypeName(string typeName)
    {
        var normalized = typeName.Trim();
        var isArray = normalized.EndsWith("[]", StringComparison.Ordinal);
        if (isArray)
        {
            normalized = normalized[..^2];
        }

        normalized = normalized switch
        {
            var value when value.Equals("boolean", StringComparison.OrdinalIgnoreCase) => "bool",
            var value when value.Equals("switch", StringComparison.OrdinalIgnoreCase) => "switch",
            var value when value.Equals("switchparameter", StringComparison.OrdinalIgnoreCase) => "switch",
            var value when value.Equals("System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase) => "switch",
            var value when value.Equals("int32", StringComparison.OrdinalIgnoreCase) => "int",
            var value when value.Equals("int64", StringComparison.OrdinalIgnoreCase) => "long",
            var value when value.Equals("System.String", StringComparison.OrdinalIgnoreCase) => "string",
            var value when value.Equals("System.Boolean", StringComparison.OrdinalIgnoreCase) => "bool",
            var value when value.Equals("System.Int32", StringComparison.OrdinalIgnoreCase) => "int",
            var value when value.Equals("System.Int64", StringComparison.OrdinalIgnoreCase) => "long",
            var value when value.Equals("System.Double", StringComparison.OrdinalIgnoreCase) => "double",
            var value when value.Equals("System.Decimal", StringComparison.OrdinalIgnoreCase) => "decimal",
            var value when value.Equals("System.Byte", StringComparison.OrdinalIgnoreCase) => "byte",
            var value when value.Equals("System.Object", StringComparison.OrdinalIgnoreCase) => "object",
            _ => normalized.ToLowerInvariant()
        };

        return isArray ? normalized + "[]" : normalized;
    }

    private async ValueTask<object?> EvaluateMemberAccessAsync(
        MemberAccessExpressionAst member,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(member.Target, cancellationToken);
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

        if (PowerShellWasmDotNetBridge.TryGetInstanceMember(target, member.MemberName, out var dotNetMember))
        {
            return dotNetMember;
        }

        if (TryGetCollectionProperty(target, member.MemberName, out var collectionValue))
        {
            return collectionValue;
        }

        return null;
    }

    private async ValueTask<object?> EvaluateStaticMemberAccessAsync(
        StaticMemberAccessExpressionAst member,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(member.Target, cancellationToken);
        if (PowerShellWasmDotNetBridge.TryGetStaticMember(target, member.MemberName, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Static member '{member.MemberName}' is not available on '{target}'.");
    }

    private async ValueTask<object?> EvaluateMethodInvocationAsync(
        MethodInvocationExpressionAst invocation,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(invocation.Target, cancellationToken);
        var arguments = new object?[invocation.Arguments.Count];
        for (var i = 0; i < invocation.Arguments.Count; i++)
        {
            arguments[i] = await EvaluateExpressionAsync(invocation.Arguments[i], cancellationToken);
        }

        if (PowerShellWasmDotNetBridge.TryInvoke(target, arguments, out var value))
        {
            return value;
        }

        throw new InvalidOperationException("Only allowlisted browser-safe .NET methods can be invoked.");
    }

    private async ValueTask<object?> EvaluateIndexAsync(IndexExpressionAst index, CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(index.Target, cancellationToken);
        var indexValue = await EvaluateExpressionAsync(index.Index, cancellationToken);
        if (TryGetDictionaryIndex(target, indexValue, out var dictionaryValue))
        {
            return dictionaryValue;
        }

        var values = ToIndexableValues(target);
        if (values.Length == 0)
        {
            return null;
        }

        var selected = new List<object?>();
        foreach (var item in Enumerate(indexValue))
        {
            var itemIndex = Convert.ToInt32(ToNumber(item), CultureInfo.InvariantCulture);
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

    private async ValueTask<object> EvaluateUnaryAsync(UnaryExpressionAst unary, CancellationToken cancellationToken)
    {
        var value = await EvaluateExpressionAsync(unary.Operand, cancellationToken);
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

    private async ValueTask<object?> EvaluateBinaryAsync(BinaryExpressionAst binary, CancellationToken cancellationToken)
    {
        var left = await EvaluateExpressionAsync(binary.Left, cancellationToken);
        if (binary.Operator == PowerShellWasmBinaryOperator.NullCoalesce)
        {
            return left ?? await EvaluateExpressionAsync(binary.Right, cancellationToken);
        }

        if (binary.Operator == PowerShellWasmBinaryOperator.LogicalAnd)
        {
            return ToBoolean(left) && ToBoolean(await EvaluateExpressionAsync(binary.Right, cancellationToken));
        }

        if (binary.Operator == PowerShellWasmBinaryOperator.LogicalOr)
        {
            return ToBoolean(left) || ToBoolean(await EvaluateExpressionAsync(binary.Right, cancellationToken));
        }

        if (binary.Operator is PowerShellWasmBinaryOperator.TypeIs or PowerShellWasmBinaryOperator.TypeIsNot or PowerShellWasmBinaryOperator.TypeAs)
        {
            return ApplyTypeOperator(left, binary.Operator, GetTypeOperatorName(binary.Right));
        }

        var right = await EvaluateExpressionAsync(binary.Right, cancellationToken);
        return ApplyBinaryOperator(left, binary.Operator, right);
    }

    private object? ApplyBinaryOperator(object? left, PowerShellWasmBinaryOperator op, object? right) =>
        op switch
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
            PowerShellWasmBinaryOperator.TypeIs => TypeMatches(left, ToInvariantString(right)),
            PowerShellWasmBinaryOperator.TypeIsNot => !TypeMatches(left, ToInvariantString(right)),
            PowerShellWasmBinaryOperator.TypeAs => TryCastAs(ToInvariantString(right), left),
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

    private static object? ApplyTypeOperator(object? left, PowerShellWasmBinaryOperator op, string typeName) =>
        op switch
        {
            PowerShellWasmBinaryOperator.TypeIs => TypeMatches(left, typeName),
            PowerShellWasmBinaryOperator.TypeIsNot => !TypeMatches(left, typeName),
            PowerShellWasmBinaryOperator.TypeAs => TryCastAs(typeName, left),
            _ => throw new InvalidOperationException($"Operator '{op}' is not a type operator.")
        };

    private static string GetTypeOperatorName(ExpressionAst expression) =>
        expression switch
        {
            TypeLiteralExpressionAst typeLiteral => typeLiteral.TypeName,
            ParenthesizedExpressionAst parenthesized => GetTypeOperatorName(parenthesized.Expression),
            StringExpressionAst text => text.Value,
            BareWordExpressionAst bareWord => bareWord.Value,
            _ => throw new InvalidOperationException("Type operators require a browser-safe type literal such as [string] or [int].")
        };

    private static object? TryCastAs(string typeName, object? value)
    {
        try
        {
            return CastValue(typeName, value);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool TypeMatches(object? value, string typeName)
    {
        if (value is null)
        {
            return false;
        }

        return NormalizeCastTypeName(typeName) switch
        {
            "object" => true,
            "string" => value is string,
            "bool" => value is bool,
            "switch" => value is bool,
            "int" => value is int,
            "long" => value is long,
            "double" => value is double,
            "decimal" => value is decimal,
            "byte" => value is byte,
            "object[]" => value is object?[],
            "string[]" => value is string[],
            "bool[]" => value is bool[],
            "switch[]" => value is bool[],
            "int[]" => value is int[],
            "long[]" => value is long[],
            "double[]" => value is double[],
            "byte[]" => value is byte[],
            _ => throw new InvalidOperationException($"Type operator target '[{typeName}]' is not available in this browser-safe runtime.")
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

        if (value is byte[])
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

    private async ValueTask<string> ExpandStringAsync(string value, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length;)
        {
            if (value[i] != '$')
            {
                builder.Append(value[i++]);
                continue;
            }

            if (TryReadSubexpression(value, i, out var script, out var nextIndex))
            {
                builder.Append(await EvaluateExpandableSubexpressionAsync(script, cancellationToken));
                i = nextIndex;
                continue;
            }

            if (TryReadBracedVariable(value, i, out var bracedName, out nextIndex))
            {
                builder.Append(ExpandVariable(bracedName));
                i = nextIndex;
                continue;
            }

            if (TryReadVariable(value, i, out var name, out nextIndex))
            {
                builder.Append(ExpandVariable(name));
                i = nextIndex;
                continue;
            }

            builder.Append(value[i++]);
        }

        return builder.ToString();
    }

    private async ValueTask<string> EvaluateExpandableSubexpressionAsync(string script, CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output))
        {
            await ExecuteAsync(new PowerShellWasmParser().Parse(script), cancellationToken);
        }

        var captured = executionContext.GetCapturedOutput(output);
        return captured.Count switch
        {
            0 => string.Empty,
            1 => ToExpandableString(captured[0]),
            _ => string.Join(executionContext.OutputFieldSeparator, captured.Select(ToExpandableString))
        };
    }

    private string ExpandVariable(string name)
    {
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return executionContext.GetEnvironmentVariable(name[4..]) ?? string.Empty;
        }

        return ToExpandableString(executionContext.GetVariable(name));
    }

    private static bool TryReadSubexpression(string value, int start, out string script, out int nextIndex)
    {
        script = string.Empty;
        nextIndex = start;
        if (start + 1 >= value.Length || value[start + 1] != '(')
        {
            return false;
        }

        var expressionStart = start + 2;
        var depth = 1;
        char? quote = null;
        for (var i = expressionStart; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch != ')')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                script = value[expressionStart..i];
                nextIndex = i + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBracedVariable(string value, int start, out string name, out int nextIndex)
    {
        name = string.Empty;
        nextIndex = start;
        if (start + 2 >= value.Length || value[start + 1] != '{')
        {
            return false;
        }

        var end = value.IndexOf('}', start + 2);
        if (end < 0)
        {
            return false;
        }

        var candidate = value[(start + 2)..end];
        if (!IsVariableName(candidate, allowColon: true))
        {
            return false;
        }

        name = candidate;
        nextIndex = end + 1;
        return true;
    }

    private static bool TryReadVariable(string value, int start, out string name, out int nextIndex)
    {
        name = string.Empty;
        nextIndex = start;
        var position = start + 1;
        var hasEnvironmentScope = false;
        if (position + 4 <= value.Length && value.AsSpan(position, 4).Equals("env:", StringComparison.OrdinalIgnoreCase))
        {
            hasEnvironmentScope = true;
            position += 4;
        }

        var nameStart = position;
        if (position >= value.Length || !IsVariableStart(value[position]))
        {
            return false;
        }

        position++;
        while (position < value.Length && IsVariablePart(value[position]))
        {
            position++;
        }

        name = hasEnvironmentScope ? "env:" + value[nameStart..position] : value[nameStart..position];
        nextIndex = position;
        return true;
    }

    private static bool IsVariableName(string name, bool allowColon)
    {
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return name.Length > 4 && IsVariableName(name[4..], allowColon: false);
        }

        if (name.Length == 0 || !IsVariableStart(name[0]))
        {
            return false;
        }

        return name.Skip(1).All(ch => allowColon && ch == ':' || IsVariablePart(ch));
    }

    private static bool IsVariableStart(char ch) =>
        ch is '_' || char.IsLetter(ch);

    private static bool IsVariablePart(char ch) =>
        ch is '_' || char.IsLetterOrDigit(ch);

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

    private void AssignParallel(IReadOnlyList<string> variableNames, object? value)
    {
        var values = Enumerate(value).ToArray();
        for (var i = 0; i < variableNames.Count; i++)
        {
            object? assignedValue;
            if (i >= values.Length)
            {
                assignedValue = null;
            }
            else if (i == variableNames.Count - 1 && values.Length > variableNames.Count)
            {
                assignedValue = values.Skip(i).ToArray();
            }
            else
            {
                assignedValue = values[i];
            }

            executionContext.SetVariable(variableNames[i], assignedValue);
        }
    }

    private void IncrementVariable(string variableName, int delta)
    {
        var value = executionContext.GetVariable(variableName);
        var number = value is null ? 0 : ToNumber(value);
        executionContext.SetVariable(variableName, NormalizeNumber(number + delta));
    }

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
