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
// - src/System.Management.Automation/engine/parser/Compiler.cs
// Ternary reference: VisitTernaryExpression-style lazy conditional branch evaluation.
// Null coalescing assignment reference: compound assignment evaluates the right operand only when the left value is null.
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
            case SettableAssignmentStatementAst assignment:
                await EvaluateSettableAssignmentAsync(
                    assignment.Target,
                    () => EvaluateExpressionAsync(assignment.Value, cancellationToken),
                    cancellationToken);
                break;
            case CompoundAssignmentStatementAst assignment:
                await EvaluateCompoundAssignmentAsync(assignment.VariableName, assignment.Operator, assignment.Value, cancellationToken);
                break;
            case SettableCompoundAssignmentStatementAst assignment:
                await EvaluateSettableCompoundAssignmentAsync(
                    assignment.Target,
                    assignment.Operator,
                    assignment.Value,
                    cancellationToken);
                break;
            case ParallelAssignmentStatementAst assignment:
                AssignParallel(assignment.VariableNames, await EvaluateExpressionAsync(assignment.Value, cancellationToken));
                break;
            case VariableIncrementStatementAst increment:
                IncrementVariable(increment.VariableName, increment.Delta);
                break;
            case SettableIncrementStatementAst increment:
                await EvaluateIncrementAsync(increment.Target, increment.Delta, isPrefix: true, cancellationToken);
                break;
            case StatementAssignmentAst assignment:
                await ExecuteStatementAssignmentAsync(assignment, cancellationToken);
                break;
            case SettableStatementAssignmentAst assignment:
                await ExecuteSettableStatementAssignmentAsync(assignment, cancellationToken);
                break;
            case ParallelStatementAssignmentAst assignment:
                await ExecuteParallelStatementAssignmentAsync(assignment, cancellationToken);
                break;
            case ExpressionStatementAst { Expression: IncrementExpressionAst increment }:
                await EvaluateIncrementAsync(increment.Target, increment.Delta, increment.IsPrefix, cancellationToken);
                break;
            case ExpressionStatementAst expression:
                await executionContext.WriteExpressionOutputAsync(await EvaluateExpressionAsync(expression.Expression, cancellationToken), cancellationToken);
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
            case DoWhileStatementAst doWhileStatement:
                await ExecuteDoWhileStatementAsync(doWhileStatement, cancellationToken);
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
                    await executionContext.WriteExpressionOutputAsync(await EvaluateExpressionAsync(returnStatement.Expression, cancellationToken), cancellationToken);
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

    private async ValueTask ExecuteDoWhileStatementAsync(DoWhileStatementAst statement, CancellationToken cancellationToken)
    {
        var iterations = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > MaximumLoopIterations)
            {
                throw new InvalidOperationException($"The browser-safe do loop exceeded {MaximumLoopIterations} iterations.");
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

            var condition = ToBoolean(await EvaluateExpressionAsync(statement.Condition, cancellationToken));
            if (statement.Until ? condition : !condition)
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

            using (executionContext.WithPipelineItem(input))
            {
                foreach (var clause in statement.Clauses)
                {
                    if (!SwitchMatches(
                        input,
                        await EvaluateExpressionAsync(clause.Pattern, cancellationToken),
                        statement.MatchMode,
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
            }

            if (breakSwitch)
            {
                break;
            }
        }
    }

    private bool SwitchMatches(object? input, object? pattern, SwitchMatchMode matchMode, bool caseSensitive) =>
        Enumerate(pattern).Any(patternItem =>
        {
            if (matchMode == SwitchMatchMode.Regex)
            {
                return RegexMatch(input, patternItem, caseSensitive);
            }

            return matchMode == SwitchMatchMode.Wildcard
                ? WildcardMatch(input, patternItem, caseSensitive)
                : CompareValues(ToInvariantString(input), ToInvariantString(patternItem), caseSensitive) == 0;
        });

    private async ValueTask ExecuteStatementAssignmentAsync(StatementAssignmentAst assignment, CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteStatementAsync(assignment.Statement, [], cancellationToken);
        }

        executionContext.SetVariable(assignment.VariableName, ToCapturedExpressionValue(executionContext.GetCapturedOutput(output)));
    }

    private async ValueTask ExecuteSettableStatementAssignmentAsync(
        SettableStatementAssignmentAst assignment,
        CancellationToken cancellationToken)
    {
        await EvaluateSettableAssignmentAsync(assignment.Target, async () =>
        {
            var output = new List<object?>();
            using (executionContext.CaptureOutput(output, captureStreams: false))
            {
                await ExecuteStatementAsync(assignment.Statement, [], cancellationToken);
            }

            return ToCapturedExpressionValue(executionContext.GetCapturedOutput(output));
        }, cancellationToken);
    }

    private async ValueTask ExecuteParallelStatementAssignmentAsync(
        ParallelStatementAssignmentAst assignment,
        CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteStatementAsync(assignment.Statement, [], cancellationToken);
        }

        AssignParallel(assignment.VariableNames, executionContext.GetCapturedOutput(output).ToArray());
    }

    private async ValueTask ExecuteStatementDiscardingOutputAsync(StatementAst statement, CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
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
        var caller = executionContext.CaptureActivation();
        var stages = new List<PipelineStage>();
        var preparedStages = new List<PreparedCommand>();
        try
        {
            // Bind each stage once in the caller before any producer starts emitting records.
            foreach (var element in pipeline.Elements)
            {
                PreparedCommand prepared;
                if (element is CommandPipelineElementAst command)
                {
                    prepared = await PrepareCommandAsync(command.Command, cancellationToken);
                }
                else if (element is ExpressionPipelineElementAst expression)
                {
                    // Native expression input is evaluated before downstream binding/Begin; delivery starts afterward.
                    var input = await EvaluateExpressionAsync(expression.Expression, cancellationToken);
                    prepared = new(PowerShellWasmCommonParameters.From(new Dictionary<string, object?>()),
                        _ => new DelegateProcessor(_ => ValueTask.CompletedTask, complete: token =>
                            executionContext.WriteExpressionOutputAsync(input, token)));
                }
                else throw new InvalidOperationException("Unsupported pipeline element.");
                preparedStages.Add(prepared);
            }
            // Common captures are initialized only after all argument expressions have observed the caller's variables.
            foreach (var prepared in preparedStages)
                stages.Add(new PipelineStage(executionContext, caller, prepared, stages.Count > 0));
            for (var index = 0; index < stages.Count - 1; index++) stages[index].Next = stages[index + 1];
            // Begin is lazy: an upstream Begin record starts its consumer before control returns to the producer.
            foreach (var stage in stages) await stage.BeginAsync(cancellationToken);
            foreach (var stage in stages) await stage.CompleteAsync(cancellationToken);
        }
        finally
        {
            foreach (var stage in stages) stage.Dispose();
        }
    }

    private async ValueTask ExecuteCommandAsync(
        CommandAst commandAst,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        var caller = executionContext.CaptureActivation();
        var prepared = await PrepareCommandAsync(commandAst, cancellationToken);
        using var stage = new PipelineStage(executionContext, caller, prepared, pipelineInput.Count > 0);
        await stage.BeginAsync(cancellationToken);
        foreach (var record in pipelineInput) await stage.ProcessAsync(record, cancellationToken);
        await stage.CompleteAsync(cancellationToken);
    }

    private async ValueTask<PreparedCommand> PrepareCommandAsync(CommandAst commandAst, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<object?>();
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var splatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownSplatArguments = new List<object?>();

        // Behavioral reference: ScriptParameterBinderController binds named arguments before positional arguments.
        // https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/scriptparameterbindercontroller.cs#L70-L100
        // Preserve expression evaluation order while interpreting adjacent values using browser-safe parameter metadata.
        var entries = commandAst.Arguments.Cast<PowerShellWasmAst>().Concat(commandAst.Parameters)
            .OrderBy(entry => entry is CommandArgumentAst argument ? argument.SourceOrder : ((CommandParameterAst)entry).SourceOrder);
        var evaluatedEntries = new List<(PowerShellWasmAst Entry, object? Value)>();
        foreach (var entry in entries)
        {
            var expression = entry is CommandArgumentAst argument ? argument.Value : ((CommandParameterAst)entry).Value;
            var value = expression is null ? true : await EvaluateExpressionAsync(expression, cancellationToken);
            evaluatedEntries.Add((entry, PowerShellWasmOutput.AsCommandArgument(value)));
        }

        var commandName = commandAst.Name;
        PowerShellWasmScriptBlock? scriptBlock = null;
        if (commandName == "&")
        {
            // Behavioral guidance: PipelineOps.AddCommand resolves an evaluated invocation target before binding.
            // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L94-L187
            // Browser scope: only scriptblocks and registered commands/functions; never files or native executables.
            if (evaluatedEntries.Count == 0 || evaluatedEntries[0].Entry is not CommandArgumentAst { IsSplat: false })
            {
                throw new InvalidOperationException("The call operator requires a script block or registered command name.");
            }

            var target = evaluatedEntries[0].Value;
            evaluatedEntries.RemoveAt(0);
            scriptBlock = target as PowerShellWasmScriptBlock;
            if (scriptBlock is null)
            {
                commandName = ToExpandableString(target);
                if (string.IsNullOrWhiteSpace(commandName))
                {
                    throw new InvalidOperationException("The call operator target cannot be null or empty.");
                }
            }
        }

        executionContext.TryGetFunction(commandName, out var function);
        commands.TryGetValue(commandName, out var command);
        var declarations = scriptBlock?.Parameters ?? function?.Parameters;

        // PowerShell evaluates argument expressions before binding, including calls that fail to bind.
        // Collect explicit names first so they override the same name in every splat, regardless of source order.
        foreach (var (entry, _) in evaluatedEntries)
        {
            if (entry is not CommandParameterAst parameter) continue;
            var declared = ResolveParameter(declarations, parameter.Name);
            var name = declared?.Name ?? parameter.Name;
            if (declarations is not null && declared is null && !IsCommonParameter(name)) continue;
            if (!explicitNames.Add(name)) throw new InvalidOperationException($"Parameter '-{name}' was specified more than once.");
        }

        foreach (var (entry, evaluatedValue) in evaluatedEntries)
        {
            if (entry is CommandArgumentAst argument)
            {
                var value = evaluatedValue;
                if (argument.IsSplat && TryAsDictionary(value, out var dictionary))
                {
                    foreach (var item in dictionary)
                    {
                        var declaration = ResolveParameter(declarations, item.Key);
                        var name = declaration?.Name ?? item.Key;
                        if (declarations is not null && declaration is null && !IsCommonParameter(name))
                        {
                            unknownSplatArguments.Add(new UnboundCommandArgument("-" + item.Key + ":"));
                            unknownSplatArguments.Add(new UnboundCommandArgument(PowerShellWasmOutput.AsCommandArgument(item.Value)));
                            continue;
                        }

                        if (explicitNames.Contains(name)) continue;
                        if (!splatNames.Add(name)) throw new InvalidOperationException($"Parameter '-{name}' was supplied by more than one splat.");
                        parameters[name] = PowerShellWasmOutput.AsCommandArgument(item.Value);
                    }
                }
                else if (argument.IsSplat)
                {
                    AddSplat(parameters, arguments, value);
                }
                else
                {
                    arguments.Add(value);
                }

                continue;
            }

            var parameter = (CommandParameterAst)entry;
            var declared = ResolveParameter(declarations, parameter.Name);
            var canonicalName = declared?.Name ?? parameter.Name;
            var parameterValue = evaluatedValue;
            if (declarations is not null && declared is null && !IsCommonParameter(canonicalName))
            {
                arguments.Add(new UnboundCommandArgument("-" + parameter.Name + (parameter.IsInlineValue ? ":" : string.Empty)));
                if (parameter.Value is not null) arguments.Add(new UnboundCommandArgument(parameterValue));
                continue;
            }

            var isSwitch = declared is not null ? IsSwitchParameter(declared)
                : IsCommonSwitch(canonicalName) || command?.SwitchParameters.Contains(canonicalName, StringComparer.OrdinalIgnoreCase) == true;
            if (isSwitch && !parameter.IsInlineValue && parameter.SourceOrder >= 0)
            {
                parameters[canonicalName] = true;
                if (parameter.Value is not null) arguments.Add(parameterValue);
            }
            else
            {
                if (declared is not null && !isSwitch && parameter.Value is null)
                {
                    throw new InvalidOperationException($"Missing argument for parameter '-{canonicalName}'.");
                }

                parameters[canonicalName] = parameterValue;
            }
        }

        arguments.AddRange(unknownSplatArguments);
        var commonParameters = PowerShellWasmCommonParameters.From(parameters);
        if (scriptBlock is not null)
        {
            return new(commonParameters, hasInput => CreateScriptProcessor(hasInput,
                (input, token) => scriptBlock.InvokeCommandAsync(new PowerShellWasmCommandContext(executionContext, parameters, arguments, input), token)));
        }

        if (function is not null)
        {
            return new(commonParameters, hasInput => CreateScriptProcessor(hasInput,
                (input, token) => ExecuteScriptFunctionAsync(function, parameters, arguments, input, token)));
        }

        if (command is null)
        {
            throw new InvalidOperationException($"Command '{commandName}' is not registered in this browser runtime.");
        }

        var context = new PowerShellWasmCommandContext(executionContext, parameters, arguments, []);
        return new(commonParameters, hasInput => command is IPowerShellWasmPipelineCommand incremental
            ? incremental.CreateProcessor(context, hasInput)
            : CreateScriptProcessor(hasInput, (input, token) => InvokeLegacyCommandAsync(command, context.WithPipelineInput(input), token)));
    }

    private sealed record UnboundCommandArgument(object? Value);

    private static bool IsSwitchParameter(ParameterDeclarationAst parameter) =>
        parameter.TypeName is not null && NormalizeCastTypeName(parameter.TypeName) == "switch";

    private static ParameterDeclarationAst? ResolveParameter(IReadOnlyList<ParameterDeclarationAst>? declarations, string name)
    {
        if (declarations is null) return null;
        var exact = declarations.FirstOrDefault(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || parameter.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var matches = declarations.Where(parameter => parameter.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            || parameter.Aliases.Any(alias => alias.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"Parameter '-{name}' is ambiguous.")
        };
    }

    private static bool IsCommonSwitch(string name) => name.ToLowerInvariant() is "debug" or "db" or "verbose" or "vb" or "whatif" or "wi";

    private static bool IsCommonParameter(string name) => IsCommonSwitch(name) || name.ToLowerInvariant() is
        "erroraction" or "ea" or "informationaction" or "infa" or "progressaction" or "proga" or "warningaction" or "wa"
        or "outvariable" or "ov" or "pipelinevariable" or "pv" or "errorvariable" or "ev" or "informationvariable" or "iv"
        or "warningvariable" or "wv";

    private async ValueTask InvokeLegacyCommandAsync(
        IPowerShellWasmCommand command, PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var capturedOutput = new List<object?>();
        ExceptionDispatchInfo? pending = null;
        using (executionContext.CaptureOutput(capturedOutput))
        {
            try
            {
                await command.InvokeAsync(context, cancellationToken);
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
        try { await executionContext.WriteCapturedOutputAsync(capturedOutput, cancellationToken); }
        catch when (pending is not null) { pending.Throw(); }
        pending?.Throw();
    }

    private static IPowerShellWasmPipelineProcessor CreateScriptProcessor(
        bool hasInput, Func<IReadOnlyList<object?>, CancellationToken, ValueTask> invoke)
    {
        var input = new List<object?>();
        return new DelegateProcessor(
            _ => ValueTask.CompletedTask,
            (record, token) => { token.ThrowIfCancellationRequested(); input.Add(record); return ValueTask.CompletedTask; },
            token => invoke(hasInput ? input : [], token));
    }

    private sealed record PreparedCommand(PowerShellWasmCommonParameters Common,
        Func<bool, IPowerShellWasmPipelineProcessor> CreateProcessor);

    private sealed class DelegateProcessor(
        Func<CancellationToken, ValueTask> begin,
        Func<object?, CancellationToken, ValueTask>? process = null,
        Func<CancellationToken, ValueTask>? complete = null) : IPowerShellWasmPipelineProcessor
    {
        public ValueTask BeginAsync(CancellationToken token) => begin(token);
        public ValueTask ProcessAsync(object? record, CancellationToken token) => process?.Invoke(record, token) ?? ValueTask.CompletedTask;
        public ValueTask CompleteAsync(CancellationToken token) => complete?.Invoke(token) ?? ValueTask.CompletedTask;
    }

    // Behavioral guidance: native Pipe.Add pushes a record into the next command before its producer resumes.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/Pipe.cs#L393-L425
    // The browser implementation awaits that handoff without worker threads or synchronous task waits.
    private sealed class PipelineStage : IDisposable
    {
        private readonly PowerShellWasmExecutionContext _context;
        private readonly PowerShellWasmExecutionContext.Activation _caller;
        private readonly PowerShellWasmCommonParameters _common;
        private readonly PowerShellWasmCommonParameters.Invocation _captures;
        private readonly IPowerShellWasmPipelineProcessor _processor;
        private bool _begun;
        internal PipelineStage? Next { get; set; }

        internal PipelineStage(PowerShellWasmExecutionContext context, PowerShellWasmExecutionContext.Activation caller,
            PreparedCommand prepared, bool hasInput)
        {
            _context = context;
            _caller = caller;
            _common = prepared.Common;
            _processor = prepared.CreateProcessor(hasInput);
            _captures = _common.CreateInvocation(context);
        }

        internal async ValueTask BeginAsync(CancellationToken token)
        {
            if (_begun) return;
            _begun = true;
            await InvokeAsync(_processor.BeginAsync, token);
        }

        internal async ValueTask ProcessAsync(object? record, CancellationToken token)
        {
            await BeginAsync(token);
            await InvokeAsync(ct => _processor.ProcessAsync(record, ct), token);
        }

        internal ValueTask CompleteAsync(CancellationToken token) => InvokeAsync(_processor.CompleteAsync, token);

        private async ValueTask InvokeAsync(Func<CancellationToken, ValueTask> invoke, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var activation = _context.Activate(_caller);
            using var scriptCaller = _context.WithScriptCaller(_caller);
            using var preferences = _common.Apply(_context);
            using var errors = _context.ObserveErrors(_captures.ObserveError);
            using var output = _context.RouteOutput(EmitAsync);
            try { await invoke(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { _context.RecordException(error); throw; }
        }

        private ValueTask EmitAsync(object? record, CancellationToken token)
        {
            record = _captures.ObserveOutput(record);
            return PowerShellWasmExecutionContext.IsStreamRecord(record) || Next is null
                ? _context.WriteToPortAsync(_caller.Output, record, token)
                : Next.ProcessAsync(record, token);
        }

        public void Dispose() => _captures.Dispose();
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

        using (executionContext.WithScriptScope(locals))
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
                parameters[item.Key] = PowerShellWasmOutput.AsCommandArgument(item.Value);
            }

            return;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                arguments.Add(PowerShellWasmOutput.AsCommandArgument(item));
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
            case SettableAssignmentExpressionAst assignment:
                return await EvaluateSettableAssignmentAsync(
                    assignment.Target, () => EvaluateExpressionAsync(assignment.Value, cancellationToken), cancellationToken);
            case CompoundAssignmentExpressionAst assignment:
                return await EvaluateCompoundAssignmentAsync(
                    assignment.VariableName,
                    assignment.Operator,
                    assignment.Value,
                    cancellationToken);
            case SettableCompoundAssignmentExpressionAst assignment:
                return await EvaluateSettableCompoundAssignmentAsync(
                    assignment.Target,
                    assignment.Operator,
                    assignment.Value,
                    cancellationToken);
            case HashtableExpressionAst hashtable:
                return await EvaluateHashtableAsync(hashtable, cancellationToken);
            case TypedHashtableExpressionAst typedHashtable:
                return await EvaluateTypedHashtableAsync(typedHashtable, cancellationToken);
            case ArrayExpressionAst array:
                return await EvaluateArrayAsync(array, cancellationToken);
            case ArraySubexpressionAst arraySubexpression:
                return await EvaluateArraySubexpressionAsync(arraySubexpression, cancellationToken);
            case SubexpressionAst subexpression:
                return await EvaluateScriptExpressionAsync(subexpression.Script, cancellationToken);
            case ScriptBlockExpressionAst scriptBlock:
                return CreateScriptBlock(scriptBlock);
            case ParenthesizedExpressionAst parenthesized:
                return await EvaluateExpressionAsync(parenthesized.Expression, cancellationToken);
            case StatementExpressionAst statement:
                return await EvaluateStatementExpressionAsync(statement.Statement, cancellationToken);
            case ScriptExpressionAst script:
                return await EvaluateScriptExpressionAsync(script.Script, cancellationToken);
            case TypeLiteralExpressionAst typeLiteral:
                return PowerShellWasmDotNetBridge.ResolveType(typeLiteral.TypeName);
            case CastExpressionAst cast:
                return await EvaluateCastAsync(cast, cancellationToken);
            case MemberAccessExpressionAst member:
                return await EvaluateMemberAccessAsync(member, cancellationToken);
            case ComputedMemberAccessExpressionAst member:
                return await EvaluateComputedMemberAccessAsync(member, cancellationToken);
            case NullConditionalMemberAccessExpressionAst member:
                return await EvaluateNullConditionalMemberAccessAsync(member, cancellationToken);
            case StaticMemberAccessExpressionAst member:
                return await EvaluateStaticMemberAccessAsync(member, cancellationToken);
            case MethodInvocationExpressionAst invocation:
                return await EvaluateMethodInvocationAsync(invocation, cancellationToken);
            case IndexExpressionAst index:
                return await EvaluateIndexAsync(index, cancellationToken);
            case UnaryExpressionAst unary:
                return await EvaluateUnaryAsync(unary, cancellationToken);
            case IncrementExpressionAst increment:
                return await EvaluateIncrementAsync(increment.Target, increment.Delta, increment.IsPrefix, cancellationToken);
            case TernaryExpressionAst ternary:
                return await EvaluateTernaryAsync(ternary, cancellationToken);
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
            _ => executionContext.GetRawVariable(name)
        };

    private async ValueTask<object?> EvaluateAssignmentAsync(AssignmentExpressionAst assignment, CancellationToken cancellationToken)
    {
        var value = await EvaluateExpressionAsync(assignment.Value, cancellationToken);
        executionContext.SetVariable(assignment.VariableName, value);
        return value;
    }

    // Behavioral guidance: Compiler's assignable values cache receivers/keys; index assignment evaluates the RHS first.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6823-L6885
    // Browser-safe adaptation: capture existing variable/dictionary/list accessors, without dynamic binders or reflection.
    private async ValueTask<object?> EvaluateSettableAssignmentAsync(
        ExpressionAst target,
        Func<ValueTask<object?>> evaluateValue,
        CancellationToken cancellationToken)
    {
        // Plain member assignment captures its receiver/name before the value; index assignment evaluates the value first.
        AssignmentTarget? location = target is MemberAccessExpressionAst or ComputedMemberAccessExpressionAst
            ? await ResolveAssignmentTargetAsync(target, cancellationToken)
            : null;
        var value = await evaluateValue();
        location ??= await ResolveAssignmentTargetAsync(target, cancellationToken);
        location.Value.Write(value);
        return value;
    }

    private async ValueTask<object?> EvaluateCompoundAssignmentAsync(
        string variableName,
        PowerShellWasmBinaryOperator op,
        ExpressionAst expression,
        CancellationToken cancellationToken)
    {
        var left = EvaluateVariableAssignmentTarget(variableName);
        if (op == PowerShellWasmBinaryOperator.NullCoalesce && PowerShellWasmOutput.AsNull(left) is not null)
        {
            return left;
        }

        var right = await EvaluateExpressionAsync(expression, cancellationToken);
        var value = op == PowerShellWasmBinaryOperator.NullCoalesce
            ? right
            : ApplyBinaryOperator(left, op, right);
        executionContext.SetVariable(variableName, value);
        return value;
    }

    private async ValueTask<object?> EvaluateSettableCompoundAssignmentAsync(
        ExpressionAst target,
        PowerShellWasmBinaryOperator op,
        ExpressionAst expression,
        CancellationToken cancellationToken)
    {
        var location = await ResolveAssignmentTargetAsync(target, cancellationToken);
        var left = location.Read();
        if (op == PowerShellWasmBinaryOperator.NullCoalesce && PowerShellWasmOutput.AsNull(left) is not null)
        {
            // PowerShell skips the right operand, but still assigns through the captured target (which may be read-only).
            location.Write(left);
            return left;
        }

        var right = await EvaluateExpressionAsync(expression, cancellationToken);
        var value = op == PowerShellWasmBinaryOperator.NullCoalesce
            ? right
            : ApplyBinaryOperator(left, op, right);
        location.Write(value);
        return value;
    }

    private object? EvaluateVariableAssignmentTarget(string variableName)
    {
        if (variableName.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return executionContext.GetEnvironmentVariable(variableName[4..]) ?? string.Empty;
        }

        return EvaluateVariable(variableName);
    }

    private async ValueTask<object?> EvaluateIncrementAsync(
        ExpressionAst target,
        int delta,
        bool isPrefix,
        CancellationToken cancellationToken)
    {
        var location = await ResolveAssignmentTargetAsync(target, cancellationToken);
        var current = location.Read();
        var updated = IncrementNumber(current, delta);
        location.Write(updated);
        return isPrefix ? updated : PowerShellWasmOutput.AsNull(current) ?? 0;
    }

    // Behavioral guidance: ReduceAssignment reuses an assignable value for the read/write, including ??= write-back.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L979-L1008
    private readonly record struct AssignmentTarget(Func<object?> Read, Action<object?> Write);

    private async ValueTask<AssignmentTarget> ResolveAssignmentTargetAsync(
        ExpressionAst target,
        CancellationToken cancellationToken)
    {
        switch (target)
        {
            case VariableExpressionAst variable:
                if (variable.IsEnvironment)
                {
                    return new AssignmentTarget(
                        () => executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty,
                        value => executionContext.SetEnvironmentVariable(variable.Name, value is null ? null : ToInvariantString(value)));
                }
                return new AssignmentTarget(
                    () => EvaluateVariable(variable.Name), value => executionContext.SetVariable(variable.Name, value));
            case MemberAccessExpressionAst member:
            {
                var receiver = await EvaluateExpressionAsync(member.Target, cancellationToken);
                return new AssignmentTarget(
                    () => EvaluateMemberAccess(receiver, member.MemberName), value => SetMemberValue(receiver, member.MemberName, value));
            }
            case ComputedMemberAccessExpressionAst member:
            {
                var receiver = await EvaluateExpressionAsync(member.Target, cancellationToken);
                var name = ToInvariantString(await EvaluateExpressionAsync(member.MemberName, cancellationToken));
                return new AssignmentTarget(
                    () => EvaluateMemberAccess(receiver, name), value => SetMemberValue(receiver, name, value));
            }
            case IndexExpressionAst index:
            {
                var receiver = await EvaluateExpressionAsync(index.Target, cancellationToken);
                var key = await EvaluateExpressionAsync(index.Index, cancellationToken);
                return new AssignmentTarget(() => EvaluateIndex(receiver, key), value => SetIndexValue(receiver, key, value));
            }
            default:
                throw new InvalidOperationException("Only variables, members, and indexers can be assigned in this browser-safe runtime.");
        }
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

    private async ValueTask<object> EvaluateTypedHashtableAsync(
        TypedHashtableExpressionAst typedHashtable,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedTypedHashtable(typedHashtable.TypeName))
        {
            throw new InvalidOperationException(
                $"Typed hashtable literal [{typedHashtable.TypeName}]@{{}} is not available in this browser-safe runtime.");
        }

        var values = await EvaluateHashtableAsync(typedHashtable.Hashtable, cancellationToken);
        var typeName = typedHashtable.TypeName.Trim();
        return typeName.Equals("pscustomobject", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Management.Automation.PSCustomObject", StringComparison.OrdinalIgnoreCase)
            ? new PowerShellWasmCustomObject(values) : values;
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

    private async ValueTask<object?[]> EvaluateArraySubexpressionAsync(
        ArraySubexpressionAst array,
        CancellationToken cancellationToken)
    {
        // Compiler.VisitArrayExpression directly evaluates a single pure expression, including ++/--.
        // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/parser/Compiler.cs#L5891-L5935
        // Multi-statement arrays and $() retain the ordinary statement-output suppression below.
        if (array.Script.Statements is [ExpressionStatementAst expression])
        {
            var value = await EvaluateExpressionAsync(expression.Expression, cancellationToken);
            if (PowerShellWasmOutput.IsNoOutput(value)) return [];
            if (value is object?[] values) return values;
            return value is System.Collections.IEnumerable items and not string and not System.Collections.IDictionary
                and not IReadOnlyDictionary<string, object?> ? items.Cast<object?>().ToArray() : [value];
        }

        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteScriptAsync(array.Script, cancellationToken);
        }

        return executionContext.GetCapturedOutput(output).ToArray();
    }

    private async ValueTask<object?> EvaluateStatementExpressionAsync(
        StatementAst statement,
        CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteStatementAsync(statement, [], cancellationToken);
        }

        return ToCapturedExpressionValue(executionContext.GetCapturedOutput(output));
    }

    private async ValueTask<object?> EvaluateScriptExpressionAsync(
        ScriptAst script,
        CancellationToken cancellationToken)
    {
        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteScriptAsync(script, cancellationToken);
        }

        return ToCapturedExpressionValue(executionContext.GetCapturedOutput(output));
    }

    private static object? ToCapturedExpressionValue(IReadOnlyList<object?> output) =>
        output.Count switch
        {
            0 => PowerShellWasmOutput.NoOutput,
            1 => output[0],
            _ => output.ToArray()
        };

    private PowerShellWasmScriptBlock CreateScriptBlock(ScriptBlockExpressionAst scriptBlock)
    {
        async ValueTask ExecuteBodyAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            CancellationToken cancellationToken)
        {
            // Script callbacks see the caller's preferences, not their enclosing cmdlet's common-parameter overrides.
            using var callerScope = executionContext.EnterScriptCaller();
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

        async ValueTask<IReadOnlyList<object?>> InvokeCoreAsync(
            object? input, IReadOnlyList<object?>? arguments, IReadOnlyDictionary<string, object?>? variables,
            bool captureStreams, CancellationToken cancellationToken)
        {
            var output = new List<object?>();
            using var outputScope = executionContext.CaptureOutput(output, captureStreams);
            await ExecuteBodyAsync(input, arguments, variables, cancellationToken);
            return captureStreams ? output : executionContext.GetCapturedOutput(output);
        }

        ValueTask<IReadOnlyList<object?>> InvokeAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            CancellationToken cancellationToken) =>
            InvokeCoreAsync(input, arguments, variables, captureStreams: false, cancellationToken);

        async ValueTask<PowerShellWasmResult> InvokeResultAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            CancellationToken cancellationToken) =>
            executionContext.CreateResult(await InvokeCoreAsync(input, arguments, variables, captureStreams: true, cancellationToken));

        async ValueTask InvokeOutputAsync(
            object? input, IReadOnlyDictionary<string, object?>? variables, CancellationToken cancellationToken)
        {
            var output = executionContext.CurrentOutputPort;
            using var outputScope = executionContext.RouteOutput((item, token) => executionContext.WriteToPortAsync(output,
                variables is null || PowerShellWasmExecutionContext.IsStreamRecord(item)
                    ? item : PowerShellWasmPipelineValue.Wrap(item, variables), token));
            await ExecuteBodyAsync(input, null, variables, cancellationToken);
        }

        var commandBody = new PowerShellWasmScriptFunction("&", scriptBlock.Body.Parameters, new ScriptAst(scriptBlock.Body.Statements));
        return new PowerShellWasmScriptBlock(InvokeAsync, InvokeResultAsync, scriptBlock.Body.Parameters,
            (context, cancellationToken) => ExecuteScriptFunctionAsync(
                commandBody, context.Parameters, context.Arguments, context.PipelineInput, cancellationToken), InvokeOutputAsync);
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
        var unboundArguments = new List<object?>();
        var argumentIndex = 0;
        var inputBound = false;

        foreach (var parameter in parameters)
        {
            locals[parameter.Name] = ConvertParameterValue(parameter, null, hasValue: false);
        }

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
            else if (!IsSwitchParameter(parameter) && TryTakePositionalArgument(arguments, ref argumentIndex, unboundArguments, out value))
            {
                wasBound = true;
                hasValue = true;
            }
            else if (!IsSwitchParameter(parameter) && bindInputToFirstParameter && !inputBound)
            {
                value = input;
                inputBound = true;
                wasBound = true;
                hasValue = true;
            }
            else
            {
                continue;
            }

            value = ConvertParameterValue(parameter, value, hasValue);
            ValidateParameterValue(parameter, value);
            locals[parameterName] = value;
            if (wasBound)
            {
                boundParameters[parameterName] = value;
            }
        }

        foreach (var parameter in parameters)
        {
            if (!boundParameters.ContainsKey(parameter.Name) && parameter.DefaultValue is not null)
            {
                var value = await EvaluateParameterDefaultAsync(parameter.DefaultValue, locals, cancellationToken);
                value = ConvertParameterValue(parameter, value, hasValue: true);
                ValidateParameterValue(parameter, value);
                locals[parameter.Name] = value;
            }
        }

        locals["PSBoundParameters"] = boundParameters;
        locals["args"] = unboundArguments.Concat(arguments.Skip(argumentIndex)
            .Select(value => value is UnboundCommandArgument unbound ? unbound.Value : value)).ToArray();
        return locals;
    }

    private static bool TryTakePositionalArgument(IReadOnlyList<object?> arguments, ref int index,
        List<object?> unboundArguments, out object? value)
    {
        while (index < arguments.Count)
        {
            value = arguments[index++];
            if (value is not UnboundCommandArgument unbound) return true;
            unboundArguments.Add(unbound.Value);
        }

        value = null;
        return false;
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
            if (value is not null and not bool) throw new InvalidOperationException($"Parameter '-{parameter.Name}' requires a Boolean switch value.");
            return hasValue && value is true;
        }

        var type = NormalizeCastTypeName(parameter.TypeName);
        return hasValue || type is "int" or "long" or "byte" or "double" or "decimal" or "bool" or "string"
            ? CastValue(parameter.TypeName, value) : value;
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
        if (typeName.Trim().Equals("void", StringComparison.OrdinalIgnoreCase) ||
            typeName.Trim().Equals("System.Void", StringComparison.OrdinalIgnoreCase))
        {
            return PowerShellWasmOutput.NoOutput;
        }

        var normalizedType = NormalizeCastTypeName(typeName);
        if (normalizedType == "object") return value;
        if (normalizedType == "string" && PowerShellWasmOutput.IsNoOutput(value)) return null;
        value = PowerShellWasmOutput.AsNull(value);
        if (PowerShellWasmDotNetBridge.TryCast(typeName, value, out var dotNetValue))
        {
            return dotNetValue;
        }

        if (normalizedType.EndsWith("[]", StringComparison.Ordinal))
        {
            return CastArrayValue(normalizedType[..^2], value);
        }

        return CastScalarValue(normalizedType, value);
    }

    private static object? CastArrayValue(string elementType, object? value)
    {
        if (PowerShellWasmOutput.AsNull(value) is null) return null;
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
        return EvaluateMemberAccess(target, member.MemberName);
    }

    private async ValueTask<object?> EvaluateComputedMemberAccessAsync(
        ComputedMemberAccessExpressionAst member,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(member.Target, cancellationToken);
        var memberName = ToInvariantString(await EvaluateExpressionAsync(member.MemberName, cancellationToken));
        return EvaluateMemberAccess(target, memberName);
    }

    private async ValueTask<object?> EvaluateNullConditionalMemberAccessAsync(
        NullConditionalMemberAccessExpressionAst member,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(member.Target, cancellationToken);
        return PowerShellWasmOutput.AsNull(target) is null ? null : EvaluateMemberAccess(target, member.MemberName);
    }

    private static object? EvaluateMemberAccess(object? target, string memberName)
    {
        target = PowerShellWasmOutput.AsNull(target);
        if (target is null)
        {
            return memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) ? 0 : null;
        }

        if (TryGetDirectMemberAccess(target, memberName, out var directValue))
        {
            return directValue;
        }

        if (TryGetEnumeratedMemberAccess(target, memberName, out var enumeratedValue))
        {
            return enumeratedValue;
        }

        return null;
    }

    private static bool TryGetDirectMemberAccess(object target, string memberName, out object? value)
    {
        value = null;
        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            readOnlyDictionary.TryGetValue(memberName, out var readOnlyValue))
        {
            value = readOnlyValue;
            return true;
        }

        if (target is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue(memberName, out var dictionaryValue))
        {
            value = dictionaryValue;
            return true;
        }

        if (target is System.Collections.IDictionary legacyDictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
            {
                if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), memberName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        if (PowerShellWasmDotNetBridge.TryGetInstanceMember(target, memberName, out var dotNetMember))
        {
            value = dotNetMember;
            return true;
        }

        if (TryGetCollectionProperty(target, memberName, out var collectionValue))
        {
            value = collectionValue;
            return true;
        }

        return false;
    }

    private static bool TryGetEnumeratedMemberAccess(object target, string memberName, out object? value)
    {
        value = null;
        if (target is string or byte[] or System.Collections.IDictionary or IReadOnlyDictionary<string, object?> ||
            target is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        var selected = new List<object?>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            selected.Add(TryGetDirectMemberAccess(item, memberName, out var itemValue) ? itemValue : null);
        }

        value = ToCapturedExpressionValue(selected);
        return true;
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
        if (invocation.Target is NullConditionalMemberAccessExpressionAst nullConditionalMember)
        {
            var nullConditionalTarget = await EvaluateExpressionAsync(nullConditionalMember.Target, cancellationToken);
            if (PowerShellWasmOutput.AsNull(nullConditionalTarget) is null)
            {
                return null;
            }

            var nullConditionalArguments = await EvaluateMethodArgumentsAsync(invocation.Arguments, cancellationToken);
            if (IsIntrinsicCollectionMethod(nullConditionalMember.MemberName))
            {
                var intrinsicResult = await TryInvokeIntrinsicCollectionMethodAsync(
                    nullConditionalTarget,
                    nullConditionalMember.MemberName,
                    nullConditionalArguments,
                    cancellationToken);
                if (intrinsicResult.Handled)
                {
                    return intrinsicResult.Value;
                }
            }

            return InvokeMethodTarget(
                GetMethodTarget(nullConditionalTarget, nullConditionalMember.MemberName),
                nullConditionalArguments);
        }

        if (invocation.Target is MemberAccessExpressionAst member &&
            IsIntrinsicCollectionMethod(member.MemberName))
        {
            var collectionTarget = await EvaluateExpressionAsync(member.Target, cancellationToken);
            var intrinsicArguments = await EvaluateMethodArgumentsAsync(invocation.Arguments, cancellationToken);
            var intrinsicResult = await TryInvokeIntrinsicCollectionMethodAsync(
                collectionTarget,
                member.MemberName,
                intrinsicArguments,
                cancellationToken);
            if (intrinsicResult.Handled)
            {
                return intrinsicResult.Value;
            }
        }

        object? target;
        if (invocation.Target is MemberAccessExpressionAst methodMember)
        {
            target = GetMethodTarget(await EvaluateExpressionAsync(methodMember.Target, cancellationToken), methodMember.MemberName);
        }
        else if (invocation.Target is ComputedMemberAccessExpressionAst computedMember)
        {
            var receiver = await EvaluateExpressionAsync(computedMember.Target, cancellationToken);
            var name = ToInvariantString(await EvaluateExpressionAsync(computedMember.MemberName, cancellationToken));
            target = GetMethodTarget(receiver, name);
        }
        else
        {
            target = await EvaluateExpressionAsync(invocation.Target, cancellationToken);
        }
        var arguments = await EvaluateMethodArgumentsAsync(invocation.Arguments, cancellationToken);

        return InvokeMethodTarget(target, arguments);
    }

    private static object? GetMethodTarget(object? receiver, string memberName)
    {
        receiver = PowerShellWasmOutput.AsNull(receiver);
        return receiver is not null && PowerShellWasmDotNetBridge.TryGetInstanceMember(receiver, memberName, out var method)
            ? method : EvaluateMemberAccess(receiver, memberName);
    }

    private static object? InvokeMethodTarget(object? target, IReadOnlyList<object?> arguments)
    {
        if (PowerShellWasmDotNetBridge.TryInvoke(target, arguments, out var value))
        {
            return value;
        }

        if (TryInvokeEnumeratedMethods(target, arguments, out var enumeratedValue))
        {
            return enumeratedValue;
        }

        throw new InvalidOperationException("Only allowlisted browser-safe .NET methods can be invoked.");
    }

    private async ValueTask<object?[]> EvaluateMethodArgumentsAsync(
        IReadOnlyList<ExpressionAst> arguments,
        CancellationToken cancellationToken)
    {
        var result = new object?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            result[i] = await EvaluateExpressionAsync(arguments[i], cancellationToken);
        }

        return result;
    }

    private static bool IsIntrinsicCollectionMethod(string memberName) =>
        memberName.Equals("ForEach", StringComparison.OrdinalIgnoreCase) ||
        memberName.Equals("Where", StringComparison.OrdinalIgnoreCase);

    private async ValueTask<(bool Handled, object? Value)> TryInvokeIntrinsicCollectionMethodAsync(
        object? target,
        string memberName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || arguments[0] is not PowerShellWasmScriptBlock scriptBlock)
        {
            return (false, null);
        }

        if (memberName.Equals("ForEach", StringComparison.OrdinalIgnoreCase))
        {
            var output = new List<object?>();
            foreach (var item in Enumerate(target))
            {
                output.AddRange(await scriptBlock.InvokeAsync(item, null, null, cancellationToken));
            }

            return (true, output.ToArray());
        }

        if (memberName.Equals("Where", StringComparison.OrdinalIgnoreCase))
        {
            var selected = new List<object?>();
            foreach (var item in Enumerate(target))
            {
                if (ToBoolean(ToCapturedExpressionValue(await scriptBlock.InvokeAsync(item, null, null, cancellationToken))))
                {
                    selected.Add(item);
                }
            }

            return (true, selected.ToArray());
        }

        return (false, null);
    }

    private static bool TryInvokeEnumeratedMethods(
        object? target,
        IReadOnlyList<object?> arguments,
        out object? value)
    {
        value = null;
        if (target is string or byte[] or System.Collections.IDictionary or IReadOnlyDictionary<string, object?> ||
            target is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        var selected = new List<object?>();
        foreach (var item in enumerable)
        {
            if (!PowerShellWasmDotNetBridge.TryInvoke(item, arguments, out var itemValue))
            {
                return false;
            }

            selected.Add(itemValue);
        }

        value = ToCapturedExpressionValue(selected);
        return true;
    }

    private async ValueTask<object?> EvaluateIndexAsync(IndexExpressionAst index, CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(index.Target, cancellationToken);
        var indexValue = await EvaluateExpressionAsync(index.Index, cancellationToken);
        return EvaluateIndex(target, indexValue);
    }

    private static object? EvaluateIndex(object? target, object? indexValue)
    {
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
        // Behavioral guidance: Compiler.VisitUnaryExpression lowers +/- to numeric 0 +/- operand.
        // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/parser/Compiler.cs#L5460-L5478
        return unary.Operator switch
        {
            PowerShellWasmUnaryOperator.Plus => NumericArithmetic(0, value, PowerShellWasmBinaryOperator.Add),
            PowerShellWasmUnaryOperator.Minus => NumericArithmetic(0, value, PowerShellWasmBinaryOperator.Subtract),
            PowerShellWasmUnaryOperator.Not => !ToBoolean(value),
            PowerShellWasmUnaryOperator.BitwiseNot => ~ToInt64(value),
            PowerShellWasmUnaryOperator.Join => string.Concat(Enumerate(value).Select(ToInvariantString)),
            PowerShellWasmUnaryOperator.Split => SplitString(ToInvariantString(value), @"\s+", ignoreCase: true),
            PowerShellWasmUnaryOperator.CaseSensitiveSplit => SplitString(ToInvariantString(value), @"\s+", ignoreCase: false),
            _ => value ?? string.Empty
        };
    }

    private async ValueTask<object?> EvaluateTernaryAsync(TernaryExpressionAst ternary, CancellationToken cancellationToken) =>
        ToBoolean(await EvaluateExpressionAsync(ternary.Condition, cancellationToken))
            ? await EvaluateExpressionAsync(ternary.IfTrue, cancellationToken)
            : await EvaluateExpressionAsync(ternary.IfFalse, cancellationToken);

    private async ValueTask<object?> EvaluateBinaryAsync(BinaryExpressionAst binary, CancellationToken cancellationToken)
    {
        var left = await EvaluateExpressionAsync(binary.Left, cancellationToken);
        if (binary.Operator == PowerShellWasmBinaryOperator.NullCoalesce)
        {
            return PowerShellWasmOutput.AsNull(left) ?? await EvaluateExpressionAsync(binary.Right, cancellationToken);
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
        if (TryApplyCollectionComparison(left, binary.Operator, right, out var collectionComparison))
        {
            return collectionComparison;
        }

        if (TryApplyCollectionStringOperator(left, binary.Operator, right, out var collectionStringValue))
        {
            return collectionStringValue;
        }

        return ApplyBinaryOperator(left, binary.Operator, right);
    }

    private bool TryApplyCollectionComparison(
        object? left,
        PowerShellWasmBinaryOperator op,
        object? right,
        out object? value)
    {
        value = null;
        if (!IsCollectionComparisonOperator(op) || !TryEnumerateCollectionOperatorOperand(left, out var items))
        {
            return false;
        }

        value = items.Where(item => ApplyCollectionComparison(item, op, right)).ToArray();
        return true;
    }

    private static bool TryEnumerateCollectionOperatorOperand(
        object? value,
        out IEnumerable<object?> items)
    {
        items = [];
        if (value is null or string or System.Collections.IDictionary or IReadOnlyDictionary<string, object?> ||
            value is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        items = enumerable.Cast<object?>();
        return true;
    }

    private static bool IsCollectionComparisonOperator(PowerShellWasmBinaryOperator op) =>
        op is PowerShellWasmBinaryOperator.Equal
            or PowerShellWasmBinaryOperator.NotEqual
            or PowerShellWasmBinaryOperator.GreaterThan
            or PowerShellWasmBinaryOperator.GreaterThanOrEqual
            or PowerShellWasmBinaryOperator.LessThan
            or PowerShellWasmBinaryOperator.LessThanOrEqual
            or PowerShellWasmBinaryOperator.Like
            or PowerShellWasmBinaryOperator.NotLike
            or PowerShellWasmBinaryOperator.Match
            or PowerShellWasmBinaryOperator.NotMatch
            or PowerShellWasmBinaryOperator.CaseSensitiveEqual
            or PowerShellWasmBinaryOperator.CaseSensitiveNotEqual
            or PowerShellWasmBinaryOperator.CaseSensitiveGreaterThan
            or PowerShellWasmBinaryOperator.CaseSensitiveGreaterThanOrEqual
            or PowerShellWasmBinaryOperator.CaseSensitiveLessThan
            or PowerShellWasmBinaryOperator.CaseSensitiveLessThanOrEqual
            or PowerShellWasmBinaryOperator.CaseSensitiveLike
            or PowerShellWasmBinaryOperator.CaseSensitiveNotLike
            or PowerShellWasmBinaryOperator.CaseSensitiveMatch
            or PowerShellWasmBinaryOperator.CaseSensitiveNotMatch;

    private bool ApplyCollectionComparison(object? item, PowerShellWasmBinaryOperator op, object? right) =>
        op switch
        {
            PowerShellWasmBinaryOperator.Equal => ValuesEqual(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotEqual => !ValuesEqual(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.GreaterThan => CompareValues(item, right, caseSensitive: false) > 0,
            PowerShellWasmBinaryOperator.GreaterThanOrEqual => CompareValues(item, right, caseSensitive: false) >= 0,
            PowerShellWasmBinaryOperator.LessThan => CompareValues(item, right, caseSensitive: false) < 0,
            PowerShellWasmBinaryOperator.LessThanOrEqual => CompareValues(item, right, caseSensitive: false) <= 0,
            PowerShellWasmBinaryOperator.Like => WildcardMatch(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotLike => !WildcardMatch(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.Match => RegexIsMatch(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotMatch => !RegexIsMatch(item, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.CaseSensitiveEqual => ValuesEqual(item, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotEqual => !ValuesEqual(item, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveGreaterThan => CompareValues(item, right, caseSensitive: true) > 0,
            PowerShellWasmBinaryOperator.CaseSensitiveGreaterThanOrEqual => CompareValues(item, right, caseSensitive: true) >= 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLessThan => CompareValues(item, right, caseSensitive: true) < 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLessThanOrEqual => CompareValues(item, right, caseSensitive: true) <= 0,
            PowerShellWasmBinaryOperator.CaseSensitiveLike => WildcardMatch(item, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotLike => !WildcardMatch(item, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveMatch => RegexIsMatch(item, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotMatch => !RegexIsMatch(item, right, caseSensitive: true),
            _ => false
        };

    private static bool TryApplyCollectionStringOperator(
        object? left,
        PowerShellWasmBinaryOperator op,
        object? right,
        out object? value)
    {
        value = null;
        if (!IsCollectionStringOperator(op) || !TryEnumerateCollectionOperatorOperand(left, out var items))
        {
            return false;
        }

        value = op switch
        {
            PowerShellWasmBinaryOperator.Replace => items
                .Select(item => ReplaceString(item, right, caseSensitive: false))
                .Cast<object?>()
                .ToArray(),
            PowerShellWasmBinaryOperator.CaseSensitiveReplace => items
                .Select(item => ReplaceString(item, right, caseSensitive: true))
                .Cast<object?>()
                .ToArray(),
            PowerShellWasmBinaryOperator.Split => items
                .SelectMany(item => SplitString(ToInvariantString(item), ToInvariantString(right), ignoreCase: true))
                .ToArray(),
            PowerShellWasmBinaryOperator.CaseSensitiveSplit => items
                .SelectMany(item => SplitString(ToInvariantString(item), ToInvariantString(right), ignoreCase: false))
                .ToArray(),
            _ => null
        };

        return value is not null;
    }

    private static bool IsCollectionStringOperator(PowerShellWasmBinaryOperator op) =>
        op is PowerShellWasmBinaryOperator.Replace
            or PowerShellWasmBinaryOperator.CaseSensitiveReplace
            or PowerShellWasmBinaryOperator.Split
            or PowerShellWasmBinaryOperator.CaseSensitiveSplit;

    private object? ApplyBinaryOperator(object? left, PowerShellWasmBinaryOperator op, object? right) =>
        op switch
        {
            PowerShellWasmBinaryOperator.Add => Add(left, right),
            PowerShellWasmBinaryOperator.Subtract => NumericArithmetic(left, right, op),
            PowerShellWasmBinaryOperator.Multiply => Multiply(left, right),
            PowerShellWasmBinaryOperator.Divide => NumericArithmetic(left, right, op),
            PowerShellWasmBinaryOperator.Remainder => NumericArithmetic(left, right, op),
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
            PowerShellWasmBinaryOperator.Equal => ValuesEqual(left, right, caseSensitive: false),
            PowerShellWasmBinaryOperator.NotEqual => !ValuesEqual(left, right, caseSensitive: false),
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
            PowerShellWasmBinaryOperator.CaseSensitiveEqual => ValuesEqual(left, right, caseSensitive: true),
            PowerShellWasmBinaryOperator.CaseSensitiveNotEqual => !ValuesEqual(left, right, caseSensitive: true),
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
            return CastValue(typeName, NormalizeCastTypeName(typeName) == "object" ? value : PowerShellWasmOutput.AsNull(value));
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool TypeMatches(object? value, string typeName)
    {
        value = PowerShellWasmOutput.AsNull(value);
        if (value is null)
        {
            return false;
        }

        if (PowerShellWasmDotNetBridge.TryTypeMatches(typeName, value, out var dotNetMatches))
        {
            return dotNetMatches;
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

    // Behavioral guidance: PSBinaryOperationBinder.BinaryAdd/BinaryMultiply dispatch on the left operand.
    // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/runtime/Binding/Binders.cs
    // Browser-safe adaptation: supported values only; no arbitrary operator overload or reflection dispatch.
    private object? Add(object? left, object? right)
    {
        left = PowerShellWasmOutput.AsNull(left);
        right = PowerShellWasmOutput.AsNull(right);
        if (left is null)
        {
            return right;
        }

        if (left is string or char)
        {
            var text = IsOperatorCollection(right)
                ? string.Join(executionContext.OutputFieldSeparator, EnumerateOperatorOperand(right).Select(ToInvariantString))
                : ToInvariantString(right);
            return ToInvariantString(left) + text;
        }

        if (IsOperatorCollection(left))
        {
            return EnumerateOperatorOperand(left).Concat(EnumerateOperatorOperand(right)).ToArray();
        }

        if (left is System.Collections.IDictionary leftDictionary)
        {
            if (right is not System.Collections.IDictionary rightDictionary)
            {
                throw new InvalidOperationException("A dictionary can only be added to another dictionary.");
            }

            var result = new PowerShellWasmHashtable();
            foreach (var dictionary in new[] { leftDictionary, rightDictionary })
            {
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    var key = ToInvariantString(entry.Key);
                    if (!result.TryAdd(key, entry.Value))
                    {
                        throw new InvalidOperationException($"The key '{key}' already exists in the combined dictionary.");
                    }
                }
            }

            return result;
        }

        return NumericArithmetic(left, right, PowerShellWasmBinaryOperator.Add);
    }

    private static object? Multiply(object? left, object? right)
    {
        left = PowerShellWasmOutput.AsNull(left);
        right = PowerShellWasmOutput.AsNull(right);
        if (left is null)
        {
            return null;
        }

        if (left is string || IsOperatorCollection(left))
        {
            var count = Convert.ToInt32(ToArithmeticNumber(right), CultureInfo.InvariantCulture);
            if (count < 0)
            {
                throw new InvalidOperationException("A repetition count cannot be negative.");
            }

            if (left is string text)
            {
                if (count == 0 || text.Length == 0)
                {
                    return string.Empty;
                }

                var result = new StringBuilder(checked(text.Length * count));
                for (var i = 0; i < count; i++)
                {
                    result.Append(text);
                }

                return result.ToString();
            }

            return left switch
            {
                string[] values => RepeatArray(values, count),
                int[] values => RepeatArray(values, count),
                long[] values => RepeatArray(values, count),
                double[] values => RepeatArray(values, count),
                decimal[] values => RepeatArray(values, count),
                byte[] values => RepeatArray(values, count),
                bool[] values => RepeatArray(values, count),
                DateTime[] values => RepeatArray(values, count),
                _ => RepeatArray(EnumerateOperatorOperand(left).ToArray(), count)
            };
        }

        if (left is bool or char)
        {
            throw new InvalidOperationException("This value does not support multiplication.");
        }

        return NumericArithmetic(left, right, PowerShellWasmBinaryOperator.Multiply);
    }

    private static T[] RepeatArray<T>(T[] values, int count)
    {
        if (values.Length == 0 || count == 0)
        {
            return [];
        }

        var result = new T[checked(values.Length * count)];
        for (var offset = 0; offset < result.Length; offset += values.Length)
        {
            Array.Copy(values, 0, result, offset, values.Length);
        }

        return result;
    }

    private static bool IsOperatorCollection(object? value) =>
        value is System.Collections.IEnumerable and not string and not System.Collections.IDictionary
            and not IReadOnlyDictionary<string, object?>;

    // Unlike pipeline enumeration, concatenation retains explicit nulls and enumerates byte arrays.
    private static IEnumerable<object?> EnumerateOperatorOperand(object? value) =>
        IsOperatorCollection(value) ? ((System.Collections.IEnumerable)value!).Cast<object?>() : [value];

    private static object ToArithmeticNumber(object? value)
    {
        value = PowerShellWasmOutput.AsNull(value);
        if (value is null) return 0;
        if (value is bool boolean) return boolean ? 1 : 0;
        if (value is byte octet) return (int)octet;
        if (value is char character) return (int)character;
        if (value is int or long or double or decimal) return value;
        if (value is string text)
        {
            return ParseArithmeticNumber(text);
        }

        throw new InvalidOperationException($"Value '{value}' is not numeric.");
    }

    private static object ParseArithmeticNumber(string text)
    {
        var token = text.Trim();
        if (token.Length == 0) return 0;

        var multiplier = 1L;
        var units = new[] { "kb", "mb", "gb", "tb", "pb" };
        for (var i = 0; i < units.Length; i++)
        {
            if (token.EndsWith(units[i], StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1L << ((i + 1) * 10);
                token = token[..^2];
                break;
            }
        }

        if (token.Length == 0) throw new InvalidOperationException($"Value '{text}' is not numeric.");
        var negative = token.StartsWith('-');
        var unsigned = token[0] is '+' or '-' ? token[1..] : token;
        var radix = unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2 : 10;
        var forceLong = token.EndsWith('l') || token.EndsWith('L');
        var forceDecimal = radix == 10 && (token.EndsWith('d') || token.EndsWith('D'));
        if (forceLong || forceDecimal)
        {
            token = token[..^1];
            unsigned = unsigned[..^1];
        }

        object value;
        if (radix != 10)
        {
            var digits = unsigned[2..];
            if (radix == 2 && digits.Length == 0
                || digits.Any(ch => radix == 2 ? ch is not ('0' or '1') : !char.IsAsciiHexDigit(ch)))
            {
                throw new InvalidOperationException($"Value '{text}' is not numeric.");
            }

            var bits = digits.Length == 0 ? 0UL : Convert.ToUInt64(digits, radix);
            if (!forceLong && (digits.Length <= (radix == 16 ? 8 : 32)
                || digits.Length < (radix == 16 ? 16 : 64) && bits <= int.MaxValue))
            {
                var integer = unchecked((int)bits);
                value = negative && integer == int.MinValue ? (object)-(long)integer : negative ? -integer : integer;
            }
            else
            {
                var integer = unchecked((long)bits);
                value = negative ? checked(-integer) : integer;
            }
        }
        else
        {
            var integerStyle = NumberStyles.Integer | NumberStyles.AllowThousands;
            var realStyle = NumberStyles.Float | NumberStyles.AllowThousands;
            if (forceDecimal)
            {
                value = decimal.Parse(token, realStyle, CultureInfo.InvariantCulture);
            }
            else if (!forceLong && int.TryParse(token, integerStyle, CultureInfo.InvariantCulture, out var integer))
            {
                value = integer;
            }
            else if (long.TryParse(token, integerStyle, CultureInfo.InvariantCulture, out var longInteger))
            {
                value = longInteger;
            }
            else
            {
                var number = double.Parse(token, realStyle, CultureInfo.InvariantCulture);
                value = forceLong ? (object)Convert.ToInt64(number) : number;
            }
        }

        if (multiplier == 1) return value;
        if (value is decimal decimalValue) return decimalValue * multiplier;
        if (value is double doubleValue) return doubleValue * multiplier;
        try
        {
            var scaled = checked(Convert.ToInt64(value, CultureInfo.InvariantCulture) * multiplier);
            return value is int && scaled is >= int.MinValue and <= int.MaxValue ? (object)(int)scaled : scaled;
        }
        catch (OverflowException)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture) * multiplier;
        }
    }

    // Behavioral guidance: PSBinaryOperationBinder selects decimal, double, or signed integer arithmetic.
    // https://github.com/PowerShell/PowerShell/blob/v7.6.1/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L2149-L2238
    // Browser-safe subset: built-in numeric values only, without user-defined overloads or reflection.
    private static object NumericArithmetic(object? left, object? right, PowerShellWasmBinaryOperator op)
    {
        var lhs = ToArithmeticNumber(left);
        var rhs = ToArithmeticNumber(right);
        if (lhs is decimal || rhs is decimal)
        {
            var a = Convert.ToDecimal(lhs, CultureInfo.InvariantCulture);
            var b = Convert.ToDecimal(rhs, CultureInfo.InvariantCulture);
            return op switch
            {
                PowerShellWasmBinaryOperator.Add => a + b,
                PowerShellWasmBinaryOperator.Subtract => a - b,
                PowerShellWasmBinaryOperator.Multiply => a * b,
                PowerShellWasmBinaryOperator.Divide => a / b,
                PowerShellWasmBinaryOperator.Remainder => a % b,
                _ => throw new InvalidOperationException($"Operator '{op}' is not numeric arithmetic.")
            };
        }

        if (lhs is not double && rhs is not double)
        {
            try
            {
                if (lhs is long || rhs is long)
                {
                    var a = Convert.ToInt64(lhs, CultureInfo.InvariantCulture);
                    var b = Convert.ToInt64(rhs, CultureInfo.InvariantCulture);
                    return op switch
                    {
                        PowerShellWasmBinaryOperator.Add => checked(a + b),
                        PowerShellWasmBinaryOperator.Subtract => checked(a - b),
                        PowerShellWasmBinaryOperator.Multiply => checked(a * b),
                        PowerShellWasmBinaryOperator.Divide => a % b == 0 ? (object)(a / b) : (double)a / b,
                        PowerShellWasmBinaryOperator.Remainder => b == -1 ? 0L : a % b,
                        _ => throw new InvalidOperationException($"Operator '{op}' is not numeric arithmetic.")
                    };
                }

                var x = (int)lhs;
                var y = (int)rhs;
                return op switch
                {
                    PowerShellWasmBinaryOperator.Add => checked(x + y),
                    PowerShellWasmBinaryOperator.Subtract => checked(x - y),
                    PowerShellWasmBinaryOperator.Multiply => checked(x * y),
                    PowerShellWasmBinaryOperator.Divide => x % y == 0 ? (object)(x / y) : (double)x / y,
                    PowerShellWasmBinaryOperator.Remainder => y == -1 ? 0 : x % y,
                    _ => throw new InvalidOperationException($"Operator '{op}' is not numeric arithmetic.")
                };
            }
            catch (OverflowException)
            {
                // PowerShell promotes overflowing integer arithmetic to double.
                // LongOps computes an exact decimal sum/difference or BigInteger product before conversion.
                // https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/runtime/Operations/NumericOps.cs#L196-L230
                if (lhs is long || rhs is long)
                {
                    var a = Convert.ToInt64(lhs, CultureInfo.InvariantCulture);
                    var b = Convert.ToInt64(rhs, CultureInfo.InvariantCulture);
                    if (op == PowerShellWasmBinaryOperator.Add) return (double)((decimal)a + b);
                    if (op == PowerShellWasmBinaryOperator.Subtract) return (double)((decimal)a - b);
                    if (op == PowerShellWasmBinaryOperator.Multiply) return (double)((System.Numerics.BigInteger)a * b);
                }
            }
        }

        var first = Convert.ToDouble(lhs, CultureInfo.InvariantCulture);
        var second = Convert.ToDouble(rhs, CultureInfo.InvariantCulture);
        return op switch
        {
            PowerShellWasmBinaryOperator.Add => first + second,
            PowerShellWasmBinaryOperator.Subtract => first - second,
            PowerShellWasmBinaryOperator.Multiply => first * second,
            PowerShellWasmBinaryOperator.Divide => first / second,
            PowerShellWasmBinaryOperator.Remainder => first % second,
            _ => throw new InvalidOperationException($"Operator '{op}' is not numeric arithmetic.")
        };
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
        string.Join(ToInvariantString(right), EnumerateOperatorOperand(left).Select(ToInvariantString));

    // Behavioral references: PSBinaryOperationBinder's scalar comparisons and LanguagePrimitives.Equals/Compare.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L2866-L3075
    // Browser-safe adaptation: left-directed conversion for supported values, without arbitrary conversion/reflection hooks.
    private bool ValuesEqual(object? left, object? right, bool caseSensitive)
    {
        left = PowerShellWasmOutput.AsNull(left);
        right = PowerShellWasmOutput.AsNull(right);
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        try
        {
            return CompareValues(left, right, caseSensitive) == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException
            or InvalidCastException or ArgumentException)
        {
            // An unsuccessful equality conversion is a non-match; ordering still reports the conversion failure.
            return false;
        }
    }

    private double CompareValues(object? left, object? right, bool caseSensitive)
    {
        left = PowerShellWasmOutput.AsNull(left);
        right = PowerShellWasmOutput.AsNull(right);
        if (left is null)
        {
            return right is null ? 0 : IsComparisonNumber(right) && CompareNumbers(right, 0) < 0 ? 1 : -1;
        }

        if (right is null)
        {
            return IsComparisonNumber(left) && CompareNumbers(left, 0) < 0 ? -1 : 1;
        }

        if (left is string text)
        {
            var rightText = IsOperatorCollection(right)
                ? string.Join(executionContext.OutputFieldSeparator, EnumerateOperatorOperand(right).Select(ToInvariantString))
                : ToInvariantString(right);
            return CultureInfo.InvariantCulture.CompareInfo.Compare(text, rightText,
                caseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase);
        }

        if (left is bool boolean)
        {
            return boolean.CompareTo(ToComparisonBoolean(right));
        }

        if (IsComparisonNumber(left))
        {
            return CompareNumbers(left, ToArithmeticNumber(right));
        }

        var supportedType = left switch
        {
            DateTime => "datetime",
            DateTimeOffset => "datetimeoffset",
            TimeSpan => "timespan",
            Guid => "guid",
            _ => null
        };
        if (supportedType is not null && left is IComparable comparable)
        {
            return comparable.CompareTo(CastValue(supportedType, right));
        }

        if (left.Equals(right)) return 0;
        throw new InvalidOperationException("These values cannot be ordered in this browser-safe runtime.");
    }

    private static bool IsComparisonNumber(object? value) => value is byte or int or long or double or decimal;

    private static double CompareNumbers(object left, object right)
    {
        if (left is decimal || right is decimal)
        {
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(
                Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }

        if (left is double || right is double)
        {
            var first = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var second = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            return double.IsNaN(first) || double.IsNaN(second) ? double.NaN : first.CompareTo(second);
        }

        return Convert.ToInt64(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(right, CultureInfo.InvariantCulture));
    }

    private static bool ToComparisonBoolean(object? value)
    {
        if (IsComparisonNumber(value)) return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;
        if (IsOperatorCollection(value))
        {
            var items = EnumerateOperatorOperand(value).ToArray();
            return items.Length > 1 || items.Length == 1 && ToComparisonBoolean(items[0]);
        }

        return ToBoolean(value);
    }

    private static bool WildcardMatch(object? left, object? right, bool caseSensitive)
    {
        var pattern = "^" + Regex.Escape(ToInvariantString(right)).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(ToInvariantString(left), pattern, RegexOptionsFor(caseSensitive));
    }

    private static bool RegexIsMatch(object? left, object? right, bool caseSensitive) =>
        Regex.IsMatch(ToInvariantString(left), ToInvariantString(right), RegexOptionsFor(caseSensitive));

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
        return Regex.Split(value, pattern, options).Cast<object?>().ToArray();
    }

    private bool Contains(object? collection, object? value, bool caseSensitive) =>
        EnumerateOperatorOperand(collection).Any(item => ValuesEqual(item, value, caseSensitive));

    private static RegexOptions RegexOptionsFor(bool caseSensitive) =>
        caseSensitive ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (PowerShellWasmOutput.AsNull(value) is null)
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
        if (PowerShellWasmOutput.AsNull(value) is null)
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

    private static void SetMemberValue(object? target, string memberName, object? value)
    {
        if (TrySetDictionaryValue(target, memberName, value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Member '{memberName}' cannot be assigned in this browser-safe runtime.");
    }

    private static void SetIndexValue(object? target, object? index, object? value)
    {
        var indexes = Enumerate(index).ToArray();
        if (indexes.Length != 1)
        {
            throw new InvalidOperationException("Index assignment requires exactly one index value.");
        }

        if (TrySetDictionaryValue(target, ToInvariantString(indexes[0]), value))
        {
            return;
        }

        if (target is System.Collections.IList list)
        {
            var itemIndex = Convert.ToInt32(ToNumber(indexes[0]), CultureInfo.InvariantCulture);
            if (itemIndex < 0)
            {
                itemIndex = list.Count + itemIndex;
            }

            if (itemIndex < 0 || itemIndex >= list.Count)
            {
                throw new InvalidOperationException($"Index {indexes[0]} is outside the target collection.");
            }

            list[itemIndex] = CoerceListItemValue(list, value);
            return;
        }

        throw new InvalidOperationException("Index assignment is supported only for dictionaries, arrays, and lists.");
    }

    // Behavioral reference: PSSetIndexBinder.SetIndexArray converts to the collection's element type before assignment.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L4343-L4354
    // Browser-safe adaptation: use only supported array/list types; object collections retain heterogeneous values.
    private static object? CoerceListItemValue(System.Collections.IList list, object? value)
    {
        var elementType = list switch
        {
            string[] or List<string> => "string",
            int[] or List<int> => "int",
            long[] or List<long> => "long",
            double[] or List<double> => "double",
            decimal[] or List<decimal> => "decimal",
            bool[] or List<bool> => "bool",
            byte[] or List<byte> => "byte",
            DateTime[] or List<DateTime> => "datetime",
            _ => null
        };

        return elementType is null ? value : CastValue(elementType, value);
    }

    private static bool TrySetDictionaryValue(object? target, string key, object? value)
    {
        switch (target)
        {
            case IDictionary<string, object?> genericDictionary:
                genericDictionary[key] = value;
                return true;
            case System.Collections.IDictionary legacyDictionary:
                foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
                {
                    if (string.Equals(ToInvariantString(entry.Key), key, StringComparison.OrdinalIgnoreCase))
                    {
                        legacyDictionary[entry.Key] = value;
                        return true;
                    }
                }

                legacyDictionary[key] = value;
                return true;
            default:
                return false;
        }
    }

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
        using (executionContext.CaptureOutput(output, captureStreams: false))
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
        PowerShellWasmOutput.AsNull(value) switch
        {
            null => 0,
            byte byteValue => byteValue,
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
        PowerShellWasmOutput.AsNull(value) switch
        {
            null => false,
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            double doubleValue => doubleValue != 0,
            decimal decimalValue => decimalValue != 0,
            string stringValue => stringValue.Length > 0,
            System.Collections.IList list => ToCollectionBoolean(list),
            _ => true
        };

    private static bool ToCollectionBoolean(System.Collections.IList values) =>
        values.Count switch
        {
            0 => false,
            1 => values[0] is System.Collections.IList nested ? nested.Count != 0 : ToBoolean(values[0]),
            _ => true
        };

    private static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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
        var value = EvaluateVariableAssignmentTarget(variableName);
        executionContext.SetVariable(variableName, IncrementNumber(value, delta));
    }

    private static object IncrementNumber(object? value, int delta)
    {
        value = PowerShellWasmOutput.AsNull(value);
        // Behavioral guidance: PSUnaryOperationBinder permits numeric values/null, not numeric strings or bools.
        // https://github.com/PowerShell/PowerShell/blob/v7.6.1/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L3338-L3367
        if (value is not (null or byte or int or long or double or decimal))
        {
            throw new InvalidOperationException("The increment and decrement operators require a numeric value.");
        }

        return NumericArithmetic(value, delta, PowerShellWasmBinaryOperator.Add);
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
