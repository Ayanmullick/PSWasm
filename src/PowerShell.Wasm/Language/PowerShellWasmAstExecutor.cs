using System.Globalization;
using System.Numerics;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
                executionContext.AssignVariable(assignment.VariableName, await EvaluateExpressionAsync(assignment.Value, cancellationToken));
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
                await ExecuteExpressionOutputAsync(expression.Expression, cancellationToken);
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
            case ThrowStatementAst throwStatement:
                await ExecuteThrowStatementAsync(throwStatement, cancellationToken);
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
                // Return compiles its operand pipeline directly; parenthesized commands still capture as expressions.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L5559-L5613
                if (returnStatement.Expression is StatementExpressionAst returnPipeline)
                {
                    await ExecuteStatementWithStatusAsync(returnPipeline.Statement, [], cancellationToken);
                }
                else if (returnStatement.Expression is not null)
                {
                    // A literal return does not reset $? before finally; commands in its operand still own their status.
                    if (returnStatement.Expression is IncrementExpressionAst increment)
                        await EvaluateIncrementAsync(increment.Target, increment.Delta, increment.IsPrefix, cancellationToken);
                    else
                        await executionContext.WriteExpressionOutputAsync(
                            await EvaluateExpressionAsync(returnStatement.Expression, cancellationToken), cancellationToken);
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
            if (await EvaluateConditionAsync(clause.Condition, cancellationToken))
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
            executionContext.AssignVariable(statement.VariableName, item);
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
        while (await EvaluateConditionAsync(statement.Condition, cancellationToken))
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

            var condition = await EvaluateConditionAsync(statement.Condition, cancellationToken);
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
        while (statement.Condition is null || await EvaluateConditionAsync(statement.Condition, cancellationToken))
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
        var output = await CaptureExpressionOutputAsync(
            () => ExecuteStatementAsync(assignment.Statement, [], cancellationToken), cancellationToken);
        executionContext.AssignVariable(assignment.VariableName, ToCapturedExpressionValue(output));
    }

    private async ValueTask ExecuteSettableStatementAssignmentAsync(
        SettableStatementAssignmentAst assignment,
        CancellationToken cancellationToken)
    {
        await EvaluateSettableAssignmentAsync(assignment.Target, async () =>
        {
            var output = await CaptureExpressionOutputAsync(
                () => ExecuteStatementAsync(assignment.Statement, [], cancellationToken), cancellationToken);
            return ToCapturedExpressionValue(output);
        }, cancellationToken);
    }

    private async ValueTask ExecuteParallelStatementAssignmentAsync(
        ParallelStatementAssignmentAst assignment,
        CancellationToken cancellationToken)
    {
        var output = await CaptureExpressionOutputAsync(
            () => ExecuteStatementAsync(assignment.Statement, [], cancellationToken), cancellationToken, flushOnControlFlow: false);
        AssignParallel(assignment.VariableNames, output.ToArray());
    }

    private async ValueTask ExecuteStatementDiscardingOutputAsync(StatementAst statement, CancellationToken cancellationToken)
    {
        await CaptureExpressionOutputAsync(() => ExecuteStatementAsync(statement, [], cancellationToken), cancellationToken);
    }

    private async ValueTask ExecuteThrowStatementAsync(ThrowStatementAst statement, CancellationToken cancellationToken)
    {
        Exception error;
        if (statement.IsRethrow && executionContext.CurrentException is { } current)
        {
            error = current;
        }
        else
        {
            var value = statement.Expression is null ? null :
                PowerShellWasmOutput.AsNull(await EvaluateExpressionAsync(statement.Expression, cancellationToken));
            if (value is null || !executionContext.TryGetErrorException(value, out error!))
            {
                var message = value is null ? "ScriptHalted" : FormatThrowMessage(value);
                error = executionContext.CreateThrowException(message);
            }
        }
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    private string FormatThrowMessage(object value)
    {
        // Native scalar custom-object conversion expands properties; nested/array custom objects use their empty ToString.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshObject.cs#L1130-L1182
        static string ItemText(object? item) => item is PowerShellWasmCustomObject ? string.Empty : ToInvariantString(item);
        return value switch
        {
            PowerShellWasmCustomObject { Count: 0 } => string.Empty,
            PowerShellWasmCustomObject custom => "@{" +
                string.Join("; ", custom.Select(item => $"{item.Key}={ItemText(item.Value)}")) + "}",
            System.Collections.IList items => string.Join(executionContext.OutputFieldSeparator, items.Cast<object?>().Select(ItemText)),
            _ => ItemText(value)
        };
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
                    using (executionContext.WithCaughtException(error))
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
            // Native pipelines retain the first stopping reason while allowing finally to run.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/pipeline.cs#L1445-L1458
            try { await ExecuteScriptAsync(statement.FinallyBlock, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (pending is StopUpstreamFlowException) { }
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
        if (script.Parameters.Count != 0) executionContext.SetParameterConstraints(script.Parameters);

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
        await ExecuteStatementWithStatusAsync(statement, [], cancellationToken);
        return executionContext.LastCommandSucceeded;
    }

    private async ValueTask ExecuteStatementWithStatusAsync(
        StatementAst statement,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteStatementAsync(statement, pipelineInput, cancellationToken);
            if (ShouldSetSuccess(statement)) executionContext.SetLastCommandSucceeded(true);
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

    // Adapt Compiler.ShouldSetExecutionStatusToSuccess: commands own their status; expressions reset only where native does.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L3256-L3404
    private static bool ShouldSetSuccess(StatementAst statement) => statement switch
    {
        ExpressionStatementAst expression => ShouldSetSuccess(expression.Expression),
        AssignmentStatementAst assignment => ShouldSetSuccess(assignment.Value),
        SettableAssignmentStatementAst assignment => ShouldSetSuccess(assignment.Value),
        CompoundAssignmentStatementAst assignment => ShouldSetSuccess(assignment.Value),
        SettableCompoundAssignmentStatementAst assignment => ShouldSetSuccess(assignment.Value),
        ParallelAssignmentStatementAst assignment => ShouldSetSuccess(assignment.Value),
        StatementAssignmentAst assignment => ShouldSetSuccess(assignment.Statement),
        SettableStatementAssignmentAst assignment => ShouldSetSuccess(assignment.Statement),
        ParallelStatementAssignmentAst assignment => ShouldSetSuccess(assignment.Statement),
        VariableIncrementStatementAst or SettableIncrementStatementAst => true,
        PipelineStatementAst { Elements: [ExpressionPipelineElementAst expression] } => ShouldSetSuccess(expression.Expression),
        _ => false
    };

    private static bool ShouldSetSuccess(ExpressionAst expression) => expression switch
    {
        ParenthesizedExpressionAst parenthesized => ShouldSetSuccess(parenthesized.Expression),
        StatementExpressionAst statement => ShouldSetSuccess(statement.Statement),
        SubexpressionAst subexpression => subexpression.Script.Statements.Count == 0,
        ScriptExpressionAst script => script.Script.Statements.Count == 0,
        ArraySubexpressionAst array => array.Script.Statements.Count == 0 ||
            (array.Script.Statements.Count == 1 && ShouldSetSuccess(array.Script.Statements[0])),
        AssignmentExpressionAst assignment => ShouldSetSuccess(assignment.Value),
        ParallelAssignmentExpressionAst assignment => ShouldSetSuccess(assignment.Value),
        SettableAssignmentExpressionAst assignment => ShouldSetSuccess(assignment.Value),
        CompoundAssignmentExpressionAst assignment => ShouldSetSuccess(assignment.Value),
        SettableCompoundAssignmentExpressionAst assignment => ShouldSetSuccess(assignment.Value),
        _ => true
    };

    // Native condition capture restores success after command-containing conditions, including a short-circuited command.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L2385-L2408
    private async ValueTask<bool> EvaluateConditionAsync(ExpressionAst expression, CancellationToken cancellationToken)
    {
        var value = expression switch
        {
            StatementExpressionAst statement => await EvaluateStatementExpressionAsync(statement.Statement, cancellationToken,
                flushOnControlFlow: false),
            ScriptExpressionAst script => ToCapturedExpressionValue(await CaptureExpressionOutputAsync(
                () => ExecuteScriptAsync(script.Script, cancellationToken), cancellationToken, flushOnControlFlow: false)),
            _ => await EvaluateExpressionAsync(expression, cancellationToken)
        };
        var result = ToBoolean(value);
        if (ContainsCommand(expression)) executionContext.SetLastCommandSucceeded(true);
        return result;
    }

    // Traverse our explicit AST, without reflection or entering deferred script-block/function bodies.
    private static bool ContainsCommand(PowerShellWasmAst? ast) => ast switch
    {
        CommandAst or CommandStatementAst or CommandPipelineElementAst => true,
        ScriptAst script => script.Statements.Any(ContainsCommand),
        ExpressionStatementAst statement => ContainsCommand(statement.Expression),
        ExpressionPipelineElementAst element => ContainsCommand(element.Expression),
        PipelineStatementAst pipeline => pipeline.Elements.Any(ContainsCommand),
        PipelineChainStatementAst chain => ContainsCommand(chain.First) || chain.Clauses.Any(ContainsCommand),
        PipelineChainClauseAst clause => ContainsCommand(clause.Statement),
        AssignmentStatementAst assignment => ContainsCommand(assignment.Value),
        SettableAssignmentStatementAst assignment => ContainsCommand(assignment.Target) || ContainsCommand(assignment.Value),
        CompoundAssignmentStatementAst assignment => ContainsCommand(assignment.Value),
        SettableCompoundAssignmentStatementAst assignment => ContainsCommand(assignment.Target) || ContainsCommand(assignment.Value),
        ParallelAssignmentStatementAst assignment => ContainsCommand(assignment.Value),
        StatementAssignmentAst assignment => ContainsCommand(assignment.Statement),
        SettableStatementAssignmentAst assignment => ContainsCommand(assignment.Target) || ContainsCommand(assignment.Statement),
        ParallelStatementAssignmentAst assignment => ContainsCommand(assignment.Statement),
        SettableIncrementStatementAst increment => ContainsCommand(increment.Target),
        TryStatementAst statement => ContainsCommand(statement.TryBlock) || statement.CatchBlocks.Any(ContainsCommand) || ContainsCommand(statement.FinallyBlock),
        IfStatementAst statement => statement.Clauses.Any(ContainsCommand) || ContainsCommand(statement.ElseBlock),
        IfClauseAst clause => ContainsCommand(clause.Condition) || ContainsCommand(clause.Body),
        ForEachStatementAst statement => ContainsCommand(statement.Collection) || ContainsCommand(statement.Body),
        WhileStatementAst statement => ContainsCommand(statement.Condition) || ContainsCommand(statement.Body),
        DoWhileStatementAst statement => ContainsCommand(statement.Condition) || ContainsCommand(statement.Body),
        ForStatementAst statement => ContainsCommand(statement.Initializer) || ContainsCommand(statement.Condition) || ContainsCommand(statement.Iterator) || ContainsCommand(statement.Body),
        SwitchStatementAst statement => ContainsCommand(statement.Input) || statement.Clauses.Any(ContainsCommand) || statement.DefaultBlocks.Any(ContainsCommand),
        SwitchClauseAst clause => ContainsCommand(clause.Pattern) || ContainsCommand(clause.Body),
        ReturnStatementAst statement => ContainsCommand(statement.Expression),
        ThrowStatementAst statement => ContainsCommand(statement.Expression),
        AssignmentExpressionAst assignment => ContainsCommand(assignment.Value),
        ParallelAssignmentExpressionAst assignment => ContainsCommand(assignment.Value),
        SettableAssignmentExpressionAst assignment => ContainsCommand(assignment.Target) || ContainsCommand(assignment.Value),
        CompoundAssignmentExpressionAst assignment => ContainsCommand(assignment.Value),
        SettableCompoundAssignmentExpressionAst assignment => ContainsCommand(assignment.Target) || ContainsCommand(assignment.Value),
        HashtableExpressionAst table => table.Entries.Any(ContainsCommand),
        HashtableEntryAst entry => ContainsCommand(entry.Key) || ContainsCommand(entry.Value),
        TypedHashtableExpressionAst table => ContainsCommand(table.Hashtable),
        ArrayExpressionAst array => array.Items.Any(ContainsCommand),
        CommandStringExpressionAst text => text.Parts.Any(ContainsCommand),
        ArraySubexpressionAst array => ContainsCommand(array.Script),
        SubexpressionAst subexpression => ContainsCommand(subexpression.Script),
        ParenthesizedExpressionAst parenthesized => ContainsCommand(parenthesized.Expression),
        StatementExpressionAst statement => ContainsCommand(statement.Statement),
        ScriptExpressionAst script => ContainsCommand(script.Script),
        CastExpressionAst cast => ContainsCommand(cast.Operand),
        MemberAccessExpressionAst member => ContainsCommand(member.Target),
        ComputedMemberAccessExpressionAst member => ContainsCommand(member.Target) || ContainsCommand(member.MemberName),
        NullConditionalMemberAccessExpressionAst member => ContainsCommand(member.Target),
        StaticMemberAccessExpressionAst member => ContainsCommand(member.Target),
        MethodInvocationExpressionAst method => ContainsCommand(method.Target) || method.Arguments.Any(ContainsCommand),
        IndexExpressionAst index => ContainsCommand(index.Target) || ContainsCommand(index.Index),
        UnaryExpressionAst unary => ContainsCommand(unary.Operand),
        IncrementExpressionAst increment => ContainsCommand(increment.Target),
        TernaryExpressionAst ternary => ContainsCommand(ternary.Condition) || ContainsCommand(ternary.IfTrue) || ContainsCommand(ternary.IfFalse),
        BinaryExpressionAst binary => ContainsCommand(binary.Left) || ContainsCommand(binary.Right),
        StringExpressionAst { IsExpandable: true } text => ContainsExpandableCommand(text),
        _ => false
    };

    private static bool ContainsExpandableCommand(StringExpressionAst text)
    {
        var value = text.Value;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '$' || text.LiteralDollarOffsets?.Contains(index) == true ||
                !TryReadSubexpression(value, index, out var script, out var nextIndex)) continue;
            if (ContainsCommand(new PowerShellWasmParser().Parse(script, text.IsInCatch, text.IsInFinally))) return true;
            index = nextIndex - 1;
        }
        return false;
    }

    private async ValueTask ExecutePipelineAsync(PipelineStatementAst pipeline, CancellationToken cancellationToken)
    {
        var status = new PowerShellWasmExecutionContext.PipelineStatus();
        using var statusScope = executionContext.WithPipelineStatus(status);
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
            // Native Select-Object stops upstream stages, then completes only the stages after the requester.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/pipeline.cs#L606-L658
            var nextBegin = 0;
            var nextComplete = 0;
            while (nextComplete < stages.Count)
            {
                try
                {
                    while (nextBegin < stages.Count) await stages[nextBegin++].BeginAsync(cancellationToken);
                    while (nextComplete < stages.Count) await stages[nextComplete++].CompleteAsync(cancellationToken);
                }
                catch (StopUpstreamFlowException stop) when (stages.Contains(stop.Stage))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status.Failed = stop.PipelineFailed;
                    var afterRequester = stages.IndexOf(stop.Stage) + 1;
                    nextBegin = Math.Max(nextBegin, afterRequester);
                    nextComplete = Math.Max(nextComplete, afterRequester);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ControlFlowException) { throw; }
        catch { status.Failed = true; throw; }
        finally
        {
            foreach (var stage in stages) stage.Dispose();
            executionContext.SetLastCommandSucceeded(!status.Failed);
        }
    }

    private async ValueTask ExecuteCommandAsync(
        CommandAst commandAst,
        IReadOnlyList<object?> pipelineInput,
        CancellationToken cancellationToken)
    {
        var status = new PowerShellWasmExecutionContext.PipelineStatus();
        using var statusScope = executionContext.WithPipelineStatus(status);
        try
        {
            var caller = executionContext.CaptureActivation();
            var prepared = await PrepareCommandAsync(commandAst, cancellationToken);
            using var stage = new PipelineStage(executionContext, caller, prepared, pipelineInput.Count > 0);
            await stage.BeginAsync(cancellationToken);
            foreach (var record in pipelineInput) await stage.ProcessAsync(record, cancellationToken);
            await stage.CompleteAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (ControlFlowException) { throw; }
        catch { status.Failed = true; throw; }
        finally
        {
            // Nested commands do not fail their caller's pipeline merely by writing a nonterminating error.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L521-L535
            executionContext.SetLastCommandSucceeded(!status.Failed);
        }
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
        var dotSource = commandName == ".";
        PowerShellWasmScriptBlock? scriptBlock = null;
        if (commandName is "&" or ".")
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
                (input, token) => scriptBlock.InvokeCommandAsync(
                    new PowerShellWasmCommandContext(executionContext, parameters, arguments, input), token, dotSource)));
        }

        if (function is not null)
        {
            return new(commonParameters, hasInput => CreateScriptProcessor(hasInput,
                (input, token) => ExecuteScriptFunctionAsync(function, parameters, arguments, input, token, dotSource)));
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
        private bool _completed;
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
            token.ThrowIfCancellationRequested();
            if (_processor.IsInputComplete) throw new StopUpstreamFlowException(this, _caller.Status?.Failed == true);
            await BeginAsync(token);
            await InvokeAsync(ct => _processor.ProcessAsync(record, ct), token);
            if (_processor.IsInputComplete)
            {
                await CompleteAsync(token);
                throw new StopUpstreamFlowException(this, _caller.Status?.Failed == true);
            }
        }

        internal async ValueTask CompleteAsync(CancellationToken token)
        {
            if (_completed) return;
            _completed = true;
            await InvokeAsync(_processor.CompleteAsync, token);
        }

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
            catch (ControlFlowException) { throw; }
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
        CancellationToken cancellationToken,
        bool dotSource = false)
    {
        // Dot-sourcing shares ordinary variables and functions but saves/restores invocation automatic variables.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ScriptCommandProcessor.cs#L625-L659
        using var dotAutomatics = dotSource ? executionContext.WithTemporaryVariables(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_"] = executionContext.GetRawVariable("_"), ["PSItem"] = executionContext.GetRawVariable("PSItem"),
                ["args"] = executionContext.GetRawVariable("args"), ["input"] = executionContext.GetRawVariable("input"),
                ["PSBoundParameters"] = executionContext.GetRawVariable("PSBoundParameters")
            }) : null;
        // Defaults execute in the same scope as the body, before invocation automatic variables receive their final values.
        // A temporary caller overlay would misresolve local:/numeric scopes and lose mutations to earlier parameters.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ScriptCommandProcessor.cs#L331-L338
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/scriptparameterbindercontroller.cs#L77-L102
        using var functionScope = dotSource ? null : executionContext.WithScriptScope(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), function.LocalTypes);
        var locals = await CreateParameterLocalsAsync(
            function.Parameters,
            parameters,
            arguments,
            null,
            bindInputToFirstParameter: false,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["input"] = pipelineInput.ToArray().GetEnumerator()
            },
            cancellationToken, (parameter, value) =>
                executionContext.SetParameterVariable(parameter, value,
                    retainConstraint: !dotSource && !PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name)));

        foreach (var name in new[] { "args", "input", "PSBoundParameters" }) executionContext.SetVariable(name, locals[name]);
        try
        {
            await ExecuteScriptAsync(function.Body, cancellationToken);
        }
        catch (ReturnFlowException)
        {
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
                return text.IsExpandable ? await ExpandStringAsync(text, cancellationToken) : text.Value;
            case CommandStringExpressionAst text:
                var argument = new StringBuilder();
                foreach (var part in text.Parts)
                    argument.Append(ToExpandableString(await EvaluateExpressionAsync(part, cancellationToken)));
                return argument.ToString();
            case VariableExpressionAst variable:
                return variable.IsEnvironment
                    ? executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty
                    : EvaluateVariable(variable.Name);
            case AssignmentExpressionAst assignment:
                return await EvaluateAssignmentAsync(assignment, cancellationToken);
            case ParallelAssignmentExpressionAst assignment:
                var parallelValue = assignment.Value is StatementExpressionAst statementValue
                    ? await EvaluateStatementExpressionAsync(statementValue.Statement, cancellationToken, flushOnControlFlow: false)
                    : await EvaluateExpressionAsync(assignment.Value, cancellationToken);
                return AssignParallel(assignment.VariableNames, parallelValue);
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
        return executionContext.AssignVariable(assignment.VariableName, value);
    }

    // Behavioral guidance: Compiler's assignable values cache receivers/keys; index assignment evaluates the RHS first.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6823-L6885
    // Browser-safe adaptation: capture existing variable/dictionary/list accessors, without dynamic binders or reflection.
    private async ValueTask<object?> EvaluateSettableAssignmentAsync(
        ExpressionAst target,
        Func<ValueTask<object?>> evaluateValue,
        CancellationToken cancellationToken)
    {
        target = UnwrapAssignmentTarget(target);
        if (target is ArrayExpressionAst arrayTarget)
            return await AssignArrayTargetsAsync(arrayTarget, await evaluateValue(), cancellationToken);
        // Plain member assignment captures its receiver/name before the value; index assignment evaluates the value first.
        var untypedTarget = target;
        while (untypedTarget is CastExpressionAst cast) untypedTarget = UnwrapAssignmentTarget(cast.Operand);
        AssignmentTarget? location = untypedTarget is MemberAccessExpressionAst or ComputedMemberAccessExpressionAst
            ? await ResolveAssignmentTargetAsync(target, cancellationToken)
            : null;
        var value = await evaluateValue();
        location ??= await ResolveAssignmentTargetAsync(target, cancellationToken);
        return location.Value.Write(value);
    }

    private async ValueTask<object?[]> AssignArrayTargetsAsync(ArrayExpressionAst targets, object? value, CancellationToken token)
    {
        var values = Enumerate(value).ToArray();
        var assigned = new object?[targets.Items.Count];
        for (var i = 0; i < targets.Items.Count; i++)
        {
            var item = i >= values.Length ? null : i == targets.Items.Count - 1 && values.Length > targets.Items.Count
                ? values.Skip(i).ToArray() : values[i];
            var target = UnwrapAssignmentTarget(targets.Items[i]);
            if (target is ArrayExpressionAst nested) await AssignArrayTargetsAsync(nested, item, token);
            else (await ResolveAssignmentTargetAsync(target, token)).Write(item);
            assigned[i] = item;
        }
        return assigned;
    }

    private static ExpressionAst UnwrapAssignmentTarget(ExpressionAst target)
    {
        while (target is ParenthesizedExpressionAst parenthesized) target = parenthesized.Expression;
        return target;
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
            return executionContext.AssignVariable(variableName, left);
        }

        var right = await EvaluateExpressionAsync(expression, cancellationToken);
        var value = op == PowerShellWasmBinaryOperator.NullCoalesce
            ? right
            : ApplyBinaryOperator(left, op, right);
        return executionContext.AssignVariable(variableName, value);
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
            return location.Write(left);
        }

        var right = await EvaluateExpressionAsync(expression, cancellationToken);
        var value = op == PowerShellWasmBinaryOperator.NullCoalesce
            ? right
            : ApplyBinaryOperator(left, op, right);
        return location.Write(value);
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
    private readonly record struct AssignmentTarget(Func<object?> Read, Func<object?, object?> Write);

    private async ValueTask<AssignmentTarget> ResolveAssignmentTargetAsync(
        ExpressionAst target,
        CancellationToken cancellationToken)
    {
        target = UnwrapAssignmentTarget(target);
        if (target is CastExpressionAst)
        {
            var types = new List<string>();
            while (target is CastExpressionAst cast)
            {
                types.Add(cast.TypeName);
                target = UnwrapAssignmentTarget(cast.Operand);
            }
            if (target is VariableExpressionAst { IsEnvironment: false } typedVariable)
                return new AssignmentTarget(() => EvaluateVariable(typedVariable.Name),
                    value => executionContext.SetTypedVariable(typedVariable.Name, value, types));
            var inner = await ResolveAssignmentTargetAsync(target, cancellationToken);
            return new AssignmentTarget(inner.Read, value =>
            {
                foreach (var type in types.AsEnumerable().Reverse())
                    value = ConvertOptimizedValue(type, value, executionContext.OutputFieldSeparator);
                return inner.Write(value);
            });
        }
        switch (target)
        {
            case VariableExpressionAst variable:
                if (variable.IsEnvironment)
                {
                    return new AssignmentTarget(
                        () => executionContext.GetEnvironmentVariable(variable.Name) ?? string.Empty,
                        value => { executionContext.SetEnvironmentVariable(variable.Name, value is null ? null : ToInvariantString(value)); return value; });
                }
                return new AssignmentTarget(
                    () => EvaluateVariable(variable.Name), value => executionContext.AssignVariable(variable.Name, value));
            case MemberAccessExpressionAst member:
            {
                var receiver = await EvaluateExpressionAsync(member.Target, cancellationToken);
                return new AssignmentTarget(
                    () => EvaluateMemberAccess(receiver, member.MemberName), value => { SetMemberValue(receiver, member.MemberName, value); return value; });
            }
            case ComputedMemberAccessExpressionAst { NullConditional: false } member:
            {
                var receiver = await EvaluateExpressionAsync(member.Target, cancellationToken);
                var name = ToInvariantString(await EvaluateExpressionAsync(member.MemberName, cancellationToken));
                return new AssignmentTarget(
                    () => EvaluateMemberAccess(receiver, name), value => { SetMemberValue(receiver, name, value); return value; });
            }
            case IndexExpressionAst index:
            {
                var receiver = await EvaluateExpressionAsync(index.Target, cancellationToken);
                var key = await EvaluateExpressionAsync(index.Index, cancellationToken);
                var keyOnly = HasObjectIndexConstraint(index.Index);
                var multipleIndexes = index.Index is ArrayExpressionAst { Items.Count: > 1 };
                return new AssignmentTarget(() => EvaluateIndex(receiver, key, keyOnly, multipleIndexes),
                    value => { SetIndexValue(receiver, key, value, keyOnly, multipleIndexes); return value; });
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
        await PopulateDictionaryAsync(result, hashtable, cancellationToken);
        return result;
    }

    // Behavioral guidance: literal keys retain their types and values are evaluated before duplicate validation.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6520-L6547
    // Browser adaptation uses BCL dictionaries with the same case-insensitive string comparer, without ETS.
    private async ValueTask PopulateDictionaryAsync(IDictionary result, HashtableExpressionAst hashtable,
        CancellationToken cancellationToken)
    {
        foreach (var entry in hashtable.Entries)
        {
            var key = PowerShellWasmOutput.AsNull(await EvaluateExpressionAsync(entry.Key, cancellationToken));
            var value = await EvaluateExpressionAsync(entry.Value, cancellationToken);
            if (key is null) throw new InvalidOperationException("A hashtable key cannot be null.");
            if (result.Contains(key)) throw new InvalidOperationException($"Duplicate key '{key}' in hashtable literal.");
            result.Add(key, value);
        }
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

        var typeName = typedHashtable.TypeName.Trim();
        var custom = typeName.Equals("pscustomobject", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Management.Automation.PSCustomObject", StringComparison.OrdinalIgnoreCase);
        if (custom || typeName.Equals("ordered", StringComparison.OrdinalIgnoreCase))
        {
            var values = new OrderedDictionary(StringComparer.OrdinalIgnoreCase);
            await PopulateDictionaryAsync(values, typedHashtable.Hashtable, cancellationToken);
            return custom ? new PowerShellWasmCustomObject(values) : values;
        }
        var table = await EvaluateHashtableAsync(typedHashtable.Hashtable, cancellationToken);
        return typeName.Equals("psobject", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Management.Automation.PSObject", StringComparison.OrdinalIgnoreCase)
            ? table : CastValue(typeName, table)!;
    }

    private static bool IsSupportedTypedHashtable(string typeName)
    {
        var normalized = typeName.Trim();
        return normalized.Equals("ordered", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("hashtable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("System.Collections.Hashtable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Collections.Specialized.OrderedDictionary", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("System.Collections.Specialized.OrderedDictionary", StringComparison.OrdinalIgnoreCase) ||
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
            var value = await EvaluateOutputExpressionAsync(expression.Expression, cancellationToken);
            if (PowerShellWasmOutput.IsNoOutput(value)) return [];
            if (value is object?[] values) return values;
            return value is (IEnumerable or IEnumerator) and not string and not System.Collections.IDictionary
                and not IReadOnlyDictionary<string, object?> ? Enumerate(value).ToArray() : [value];
        }

        var output = new List<object?>();
        using (executionContext.CaptureOutput(output, captureStreams: false))
        {
            await ExecuteScriptAsync(array.Script, cancellationToken);
        }

        return executionContext.GetCapturedOutput(output).ToArray();
    }

    private async ValueTask ExecuteExpressionOutputAsync(ExpressionAst expression, CancellationToken cancellationToken)
    {
        await executionContext.WriteExpressionOutputAsync(
            await EvaluateOutputExpressionAsync(expression, cancellationToken), cancellationToken);
    }

    private async ValueTask<object?> EvaluateOutputExpressionAsync(ExpressionAst expression, CancellationToken cancellationToken)
    {
        var unwrapped = expression;
        while (unwrapped is ParenthesizedExpressionAst parenthesized) unwrapped = parenthesized.Expression;
        // A standalone captured pipeline retains partial success output on failure. In an operand or argument it does not.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/ast.cs#L7364-L7397
        return unwrapped switch
        {
            StatementExpressionAst statement => await EvaluateStatementExpressionAsync(statement.Statement, cancellationToken,
                flushOnError: true),
            SubexpressionAst subexpression => await EvaluateScriptExpressionAsync(subexpression.Script, cancellationToken,
                flushOnError: true),
            ScriptExpressionAst script => await EvaluateScriptExpressionAsync(script.Script, cancellationToken, flushOnError: true),
            ArraySubexpressionAst array => await EvaluateArraySubexpressionAsync(array, cancellationToken),
            _ => await EvaluateExpressionAsync(expression, cancellationToken)
        };
    }

    private async ValueTask<object?> EvaluateStatementExpressionAsync(
        StatementAst statement,
        CancellationToken cancellationToken,
        bool flushOnControlFlow = true,
        bool flushOnError = false)
    {
        var output = await CaptureExpressionOutputAsync(
            () => ExecuteStatementAsync(statement, [], cancellationToken), cancellationToken, flushOnControlFlow, flushOnError);
        return ToCapturedExpressionValue(output);
    }

    private async ValueTask<object?> EvaluateScriptExpressionAsync(
        ScriptAst script,
        CancellationToken cancellationToken,
        bool flushOnError = false)
    {
        var output = await CaptureExpressionOutputAsync(() => ExecuteScriptAsync(script, cancellationToken), cancellationToken,
            flushOnError: flushOnError);
        return ToCapturedExpressionValue(output);
    }

    // Assignment-style captures flush on return/break/continue, but discard partial output for an error.
    // Enumerable and command-condition captures deliberately do not flush; upstream stopping remains independent.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L2250-L2340
    private async ValueTask<IReadOnlyList<object?>> CaptureExpressionOutputAsync(
        Func<ValueTask> execute, CancellationToken cancellationToken, bool flushOnControlFlow = true, bool flushOnError = false)
    {
        var output = new List<object?>();
        try
        {
            using (executionContext.CaptureOutput(output, captureStreams: false)) await execute();
        }
        catch (ControlFlowException flow) when (flushOnControlFlow && flow is not StopUpstreamFlowException)
        {
            await executionContext.WriteCapturedOutputAsync(output, cancellationToken);
            throw;
        }
        catch (Exception error) when (flushOnError && error is not (ControlFlowException or OperationCanceledException))
        {
            await executionContext.WriteCapturedOutputAsync(output, cancellationToken);
            throw;
        }
        return executionContext.GetCapturedOutput(output);
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
        IReadOnlyDictionary<string, string>? localTypes = null;
        async ValueTask ExecuteBodyAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            PowerShellWasmScriptBlockInvocation invocation,
            CancellationToken cancellationToken)
        {
            // Script callbacks see the caller's preferences, not their enclosing cmdlet's common-parameter overrides.
            using var callerScope = invocation == PowerShellWasmScriptBlockInvocation.Collection
                ? null : executionContext.EnterScriptCaller();
            if (invocation == PowerShellWasmScriptBlockInvocation.Host)
            {
                var locals = await CreateParameterLocalsAsync(scriptBlock.Body.Parameters, null, arguments ?? [], input,
                    bindInputToFirstParameter: arguments is null, variables, cancellationToken);
                using var variableScope = executionContext.WithTemporaryVariables(locals);
                using var pipelineScope = executionContext.WithPipelineItem(input);
                executionContext.SetParameterConstraints(scriptBlock.Body.Parameters);
                await ExecuteStatementsAsync(cancellationToken);
                return;
            }

            // Cmdlet callbacks run in caller scope with a one-record input array; collection methods inherit input.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/InternalCommands.cs#L919-L934
            // Calculated properties instead use a child scope; none of these callbacks implicitly bind $_ as an argument.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/FormatAndOutput/common/Utilities/Mshexpression.cs#L310-L325
            var callbackLocals = variables is null ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
            callbackLocals["args"] = executionContext.GetRawVariable("args");
            callbackLocals["PSBoundParameters"] = executionContext.GetRawVariable("PSBoundParameters");
            callbackLocals["_"] = PowerShellWasmOutput.IsNoOutput(input) ? executionContext.GetRawVariable("_") : input;
            callbackLocals["input"] = invocation switch
            {
                PowerShellWasmScriptBlockInvocation.Pipeline => new object?[] { input },
                PowerShellWasmScriptBlockInvocation.PipelineBeginEnd or PowerShellWasmScriptBlockInvocation.Delegate
                    => Array.Empty<object?>(),
                _ => executionContext.GetRawVariable("input")
            };

            // List action delegates also use a fresh child scope, with the item passed as an argument and inherited $_.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/lang/scriptblock.cs#L757-L810
            var childScope = invocation is PowerShellWasmScriptBlockInvocation.CalculatedProperty
                or PowerShellWasmScriptBlockInvocation.Delegate;
            if (!childScope)
            {
                foreach (var parameter in scriptBlock.Body.Parameters)
                {
                    if (PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name)) continue;
                    var name = PowerShellWasmExecutionContext.GetParameterVariableName(parameter.Name);
                    if (!executionContext.HasVariableInCurrentScope(name) && !callbackLocals.ContainsKey(name))
                        callbackLocals[name] = executionContext.GetRawVariable(parameter.Name);
                }
            }
            using var callbackScope = childScope ? executionContext.WithScriptScope(callbackLocals,
                localTypes ??= PowerShellWasmVariableAnalysis.Analyze(scriptBlock.Body))
                : executionContext.WithTemporaryVariables(callbackLocals);
            var boundNames = new List<string>();
            var parameterBackups = new List<IDisposable>();
            try
            {
                await BindCallbackParametersAsync(scriptBlock.Body.Parameters, arguments ?? [], boundNames,
                    childScope ? null : parameterBackups, cancellationToken);
                await ExecuteStatementsAsync(cancellationToken);
            }
            finally
            {
                executionContext.ClearParameterConstraints(boundNames);
                foreach (var backup in parameterBackups) backup.Dispose();
            }
        }

        async ValueTask ExecuteStatementsAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var statement in scriptBlock.Body.Statements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExecuteStatementWithStatusAsync(statement, [], cancellationToken);
                }
            }
            catch (ReturnFlowException)
            {
            }
        }

        async ValueTask<IReadOnlyList<object?>> InvokeCoreAsync(
            object? input, IReadOnlyList<object?>? arguments, IReadOnlyDictionary<string, object?>? variables,
            PowerShellWasmScriptBlockInvocation invocation, bool captureStreams, CancellationToken cancellationToken)
        {
            var output = new List<object?>();
            using var outputScope = executionContext.CaptureOutput(output, captureStreams);
            await ExecuteBodyAsync(input, arguments, variables, invocation, cancellationToken);
            return captureStreams ? output : executionContext.GetCapturedOutput(output);
        }

        ValueTask<IReadOnlyList<object?>> InvokeAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            PowerShellWasmScriptBlockInvocation invocation,
            CancellationToken cancellationToken) =>
            InvokeCoreAsync(input, arguments, variables, invocation, captureStreams: false, cancellationToken);

        async ValueTask<PowerShellWasmResult> InvokeResultAsync(
            object? input,
            IReadOnlyList<object?>? arguments,
            IReadOnlyDictionary<string, object?>? variables,
            PowerShellWasmScriptBlockInvocation invocation,
            CancellationToken cancellationToken) =>
            executionContext.CreateResult(await InvokeCoreAsync(input, arguments, variables, invocation, captureStreams: true, cancellationToken));

        async ValueTask InvokeOutputAsync(
            object? input, IReadOnlyDictionary<string, object?>? variables,
            PowerShellWasmScriptBlockInvocation invocation, CancellationToken cancellationToken)
        {
            var output = executionContext.CurrentOutputPort;
            using var outputScope = executionContext.RouteOutput((item, token) => executionContext.WriteToPortAsync(output,
                variables is null || PowerShellWasmExecutionContext.IsStreamRecord(item)
                    ? item : PowerShellWasmPipelineValue.Wrap(item, variables), token));
            await ExecuteBodyAsync(input, null, variables, invocation, cancellationToken);
        }

        var commandBody = new PowerShellWasmScriptFunction("&", scriptBlock.Body.Parameters, new ScriptAst(scriptBlock.Body.Statements));
        return new PowerShellWasmScriptBlock(InvokeAsync, InvokeResultAsync, scriptBlock.Body.Parameters,
            (context, dotSource, cancellationToken) => ExecuteScriptFunctionAsync(
                commandBody, context.Parameters, context.Arguments, context.PipelineInput, cancellationToken, dotSource),
            InvokeOutputAsync, scriptBlock.SourceText);
    }

    // Callback arguments/defaults bind sequentially in the invocation scope; only declared parameters replace
    // PSBoundParameters. Existing parameter variables retain their mutations; newly introduced ones are temporary.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/CompiledScriptBlock.cs#L1288-L1355
    private async ValueTask BindCallbackParametersAsync(IReadOnlyList<ParameterDeclarationAst> parameters,
        IReadOnlyList<object?> arguments, List<string> boundNames, List<IDisposable>? parameterBackups,
        CancellationToken cancellationToken)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var emptyLocals = new Dictionary<string, object?>();
        var unbound = new List<object?>();
        var argumentIndex = 0;
        foreach (var parameter in parameters)
        {
            boundNames.Add(parameter.Name);
            object? value = null;
            var wasBound = TryTakePositionalArgument(arguments, ref argumentIndex, unbound, out value);
            var hasValue = wasBound || parameter.DefaultValue is not null;
            if (!wasBound && parameter.DefaultValue is not null)
                value = await EvaluateParameterDefaultAsync(parameter.DefaultValue, emptyLocals, cancellationToken);
            if (parameterBackups is not null && PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name))
                parameterBackups.Add(executionContext.BackUpRootScopedCallbackParameter(parameter.Name));
            value = ConvertParameterValue(parameter, value, hasValue);
            ValidateParameterValue(parameter, value is null ? new object?[] { null } : value);
            executionContext.SetParameterVariable(parameter, value, retainConstraint: true);
            if (wasBound) bound[parameter.Name] = value;
        }
        executionContext.SetVariable("args", unbound.Concat(arguments.Skip(argumentIndex)).ToArray());
        if (parameters.Count != 0) executionContext.SetVariable("PSBoundParameters", bound);
    }

    private async ValueTask<Dictionary<string, object?>> CreateParameterLocalsAsync(
        IReadOnlyList<ParameterDeclarationAst> parameters,
        IReadOnlyDictionary<string, object?>? namedParameters,
        IReadOnlyList<object?> arguments,
        object? input,
        bool bindInputToFirstParameter,
        IReadOnlyDictionary<string, object?>? variables,
        CancellationToken cancellationToken,
        Action<ParameterDeclarationAst, object?>? bindInCurrentScope = null)
    {
        var locals = variables is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
        var boundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var unboundArguments = new List<object?>();
        var argumentIndex = 0;
        var inputBound = false;
        var parameterValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            var value = ConvertParameterValue(parameter, null, hasValue: false);
            parameterValues[parameter.Name] = value;
            if (!PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name)) locals[parameter.Name] = value;
        }

        void BindParameter(ParameterDeclarationAst parameter, object? value)
        {
            parameterValues[parameter.Name] = value;
            if (!PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name)) locals[parameter.Name] = value;
            if (bindInCurrentScope is not null) bindInCurrentScope(parameter, value);
            else if (PowerShellWasmExecutionContext.IsRootScopedParameter(parameter.Name))
                executionContext.SetParameterVariable(parameter, value, retainConstraint: false);
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
            // Successful dot-sourced bindings survive a later parameter conversion/validation failure.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/scriptparameterbinder.cs#L101-L135
            BindParameter(parameter, value);
            if (wasBound)
            {
                boundParameters[parameterName] = value;
            }
        }

        foreach (var parameter in parameters)
        {
            if (boundParameters.ContainsKey(parameter.Name)) continue;
            if (parameter.DefaultValue is not null)
            {
                var value = await EvaluateParameterDefaultAsync(parameter.DefaultValue,
                    bindInCurrentScope is null ? locals : null, cancellationToken);
                value = ConvertParameterValue(parameter, value, hasValue: true);
                ValidateParameterValue(parameter, value);
                parameterValues[parameter.Name] = value;
            }
            BindParameter(parameter, parameterValues[parameter.Name]);
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

    private object? ConvertParameterValue(ParameterDeclarationAst parameter, object? value, bool hasValue)
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
        if (type == "bool") return hasValue ? ConvertConstrainedValue(type, value, executionContext.OutputFieldSeparator) : false;
        if (type == "string" && value is IEnumerator) return ToExpandableString(value);
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

    // Default-value expressions have a return target and output pipe separate from the function body.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L2131-L2211
    private async ValueTask<object?> EvaluateParameterDefaultAsync(
        ExpressionAst expression,
        IReadOnlyDictionary<string, object?>? locals,
        CancellationToken cancellationToken)
    {
        using var parameterScope = locals is null || locals.Count == 0 ? null : executionContext.WithTemporaryVariables(locals);
        var output = new List<object?>();
        using var outputScope = executionContext.CaptureOutput(output, captureStreams: false);
        try
        {
            return PowerShellWasmOutput.AsNull(await EvaluateExpressionAsync(expression, cancellationToken));
        }
        catch (ReturnFlowException)
        {
            return PowerShellWasmOutput.AsNull(ToCapturedExpressionValue(executionContext.GetCapturedOutput(output)));
        }
        catch (ControlFlowException flow) when (flow is LoopBreakFlowException or LoopContinueFlowException)
        {
            return null;
        }
    }

    private async ValueTask<object?> EvaluateCastAsync(CastExpressionAst cast, CancellationToken cancellationToken)
    {
        var value = await EvaluateExpressionAsync(cast.Operand, cancellationToken);
        if (NormalizeCastTypeName(cast.TypeName) == "string" && value is IEnumerator) return ToExpandableString(value);
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
        if (normalizedType is "array" or "system.array")
            return value is null or Array ? value : Enumerate(value).ToArray();
        if (normalizedType is "pscustomobject" or "system.management.automation.pscustomobject")
        {
            if (value is null or PowerShellWasmCustomObject) return value;
            if (value is IDictionary properties) return new PowerShellWasmCustomObject(properties);
            throw new InvalidOperationException("A custom object cast requires a dictionary in this browser-safe runtime.");
        }
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

    // ArgumentTypeConverterAttribute enforces scalar Boolean/switch constraints more strictly than an explicit cast.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ArgumentTypeConverterAttribute.cs
    internal static object? ConvertConstrainedValue(string typeName, object? value, string separator)
    {
        var type = NormalizeCastTypeName(typeName);
        if (type is "void" or "system.void") throw new InvalidOperationException("A variable cannot have type void.");
        if (type is "bool" or "switch")
        {
            value = PowerShellWasmOutput.AsNull(value);
            if (value is bool boolean) return boolean;
            if (type == "switch" && value is null) return false;
            if (type == "bool" && IsNumericValue(value))
                return ToBoolean(value);
            throw new InvalidOperationException($"Cannot convert this value to type [{typeName}].");
        }
        if (type == "string" && value is IEnumerable and not string and not IDictionary or IEnumerator)
            return string.Join(separator, Enumerate(value).Select(ToInvariantString));
        return CastValue(typeName, value);
    }

    // Statically typed local tuple fields use a language conversion rather than PSVariable attribute transformation.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/ast.cs#L9504-L9549
    internal static object? ConvertOptimizedValue(string typeName, object? value, string separator) =>
        NormalizeCastTypeName(typeName) is "bool" or "switch" ? CastValue(typeName, value)
            : ConvertConstrainedValue(typeName, value, separator);

    internal static void ValidateConstrainedValue(IReadOnlyList<string> validValues, object? value)
    {
        if (validValues.Count == 0) return;
        foreach (var item in value is null ? new object?[] { null } : Enumerate(value))
            if (!validValues.Contains(ToInvariantString(item), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Variable failed ValidateSet. Expected one of: {string.Join(", ", validValues)}.");
    }

    private static object? CastArrayValue(string elementType, object? value)
    {
        if (PowerShellWasmOutput.AsNull(value) is null) return null;
        if (elementType == "char" && value is string text) return text.ToCharArray();
        // Conversion to an assignable array type preserves identity, including reference-array covariance.
        var alreadyAssignable = elementType switch
        {
            "byte" => value is byte[],
            "sbyte" => value is sbyte[],
            "short" => value is short[],
            "ushort" => value is ushort[],
            "uint" => value is uint[],
            "ulong" => value is ulong[],
            "float" => value is float[],
            "decimal" => value is decimal[],
            "bigint" => value is BigInteger[],
            "char" => value is char[],
            "string" => value is string[],
            "int" => value is int[],
            "long" => value is long[],
            "double" => value is double[],
            "bool" or "switch" => value is bool[],
            "object" => value is object[],
            _ => false
        };
        if (alreadyAssignable) return value;
        var values = Enumerate(value).Select(item => CastScalarValue(elementType, item)).ToArray();
        return elementType switch
        {
            "byte" => values.Select(item => Convert.ToByte(item, CultureInfo.InvariantCulture)).ToArray(),
            "sbyte" => values.Cast<sbyte>().ToArray(),
            "short" => values.Cast<short>().ToArray(),
            "ushort" => values.Cast<ushort>().ToArray(),
            "uint" => values.Cast<uint>().ToArray(),
            "ulong" => values.Cast<ulong>().ToArray(),
            "float" => values.Cast<float>().ToArray(),
            "decimal" => values.Cast<decimal>().ToArray(),
            "bigint" => values.Cast<BigInteger>().ToArray(),
            "char" => values.Cast<char>().ToArray(),
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
            "char" => ToCharacter(value),
            "bool" => ToBoolean(value),
            "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
                or "float" or "double" or "decimal" or "bigint" => ConvertNumericValue(typeName, value),
            "switch" => ToBoolean(value),
            _ => throw new InvalidOperationException($"Cast type '[{typeName}]' is not available in this browser-safe runtime.")
        };

    // Character conversion accepts one UTF-16 code unit or an integral code; floating-point and Boolean values reject.
    // String-to-char-array conversion above preserves UTF-16 units, including surrogate pairs.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs#L4478-L4485
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs#L4564-L4575
    private static char ToCharacter(object? value) => PowerShellWasmOutput.AsNull(value) switch
    {
        null => '\0',
        char character => character,
        string text when text.Length == 1 => text[0],
        byte or sbyte or short or ushort or int or uint or long or ulong => (char)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("The value cannot be converted to System.Char.")
    };

    // Keep integral and decimal values exact; going through Double loses Int64/UInt64 precision.
    // BigInteger conversions are explicit because it deliberately does not implement IConvertible.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs
    private static object ConvertNumericValue(string typeName, object? value)
    {
        // Native tries the numeric scanner first; target-specific parsing is a fallback for grouped strings.
        if (typeName == "decimal" && value is string text &&
            !PowerShellWasmNumericLiteral.TryParse(text.Trim(), out _) &&
            decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var grouped))
            return grouped;
        var number = ToArithmeticNumber(value);
        if (typeName == "bigint") return ToBigInteger(number);
        if (number is BigInteger big)
            return typeName switch
            {
                "sbyte" => (object)(sbyte)big, "byte" => (byte)big, "short" => (short)big, "ushort" => (ushort)big,
                "int" => (int)big, "uint" => (uint)big, "long" => (long)big, "ulong" => (ulong)big,
                "float" => (float)big, "double" => (double)big, "decimal" => (decimal)big,
                _ => throw new InvalidOperationException($"Unsupported numeric conversion [{typeName}].")
            };
        return typeName switch
        {
            "sbyte" => (object)Convert.ToSByte(number, CultureInfo.InvariantCulture),
            "byte" => Convert.ToByte(number, CultureInfo.InvariantCulture),
            "short" => Convert.ToInt16(number, CultureInfo.InvariantCulture),
            "ushort" => Convert.ToUInt16(number, CultureInfo.InvariantCulture),
            "int" => Convert.ToInt32(number, CultureInfo.InvariantCulture),
            "uint" => Convert.ToUInt32(number, CultureInfo.InvariantCulture),
            "long" => Convert.ToInt64(number, CultureInfo.InvariantCulture),
            "ulong" => Convert.ToUInt64(number, CultureInfo.InvariantCulture),
            "float" => Convert.ToSingle(number, CultureInfo.InvariantCulture),
            "double" => Convert.ToDouble(number, CultureInfo.InvariantCulture),
            "decimal" => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported numeric conversion [{typeName}].")
        };
    }

    internal static string NormalizeCastTypeName(string typeName)
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
            var value when value.Equals("int16", StringComparison.OrdinalIgnoreCase) => "short",
            var value when value.Equals("uint16", StringComparison.OrdinalIgnoreCase) => "ushort",
            var value when value.Equals("uint32", StringComparison.OrdinalIgnoreCase) => "uint",
            var value when value.Equals("uint64", StringComparison.OrdinalIgnoreCase) => "ulong",
            var value when value.Equals("single", StringComparison.OrdinalIgnoreCase) => "float",
            var value when value.Equals("biginteger", StringComparison.OrdinalIgnoreCase)
                || value.Equals("numerics.biginteger", StringComparison.OrdinalIgnoreCase)
                || value.Equals("system.numerics.biginteger", StringComparison.OrdinalIgnoreCase) => "bigint",
            var value when value.Equals("System.String", StringComparison.OrdinalIgnoreCase) => "string",
            var value when value.Equals("System.Char", StringComparison.OrdinalIgnoreCase) => "char",
            var value when value.Equals("System.Boolean", StringComparison.OrdinalIgnoreCase) => "bool",
            var value when value.Equals("System.Int32", StringComparison.OrdinalIgnoreCase) => "int",
            var value when value.Equals("System.Int64", StringComparison.OrdinalIgnoreCase) => "long",
            var value when value.Equals("System.Double", StringComparison.OrdinalIgnoreCase) => "double",
            var value when value.Equals("System.Decimal", StringComparison.OrdinalIgnoreCase) => "decimal",
            var value when value.Equals("System.Byte", StringComparison.OrdinalIgnoreCase) => "byte",
            var value when value.Equals("System.SByte", StringComparison.OrdinalIgnoreCase) => "sbyte",
            var value when value.Equals("System.Int16", StringComparison.OrdinalIgnoreCase) => "short",
            var value when value.Equals("System.UInt16", StringComparison.OrdinalIgnoreCase) => "ushort",
            var value when value.Equals("System.UInt32", StringComparison.OrdinalIgnoreCase) => "uint",
            var value when value.Equals("System.UInt64", StringComparison.OrdinalIgnoreCase) => "ulong",
            var value when value.Equals("System.Single", StringComparison.OrdinalIgnoreCase) => "float",
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
        if (member.NullConditional && PowerShellWasmOutput.AsNull(target) is null) return null;
        if (member.NullConditional) target = await EvaluateExpressionAsync(member.Target, cancellationToken);
        var memberName = ToInvariantString(await EvaluateExpressionAsync(member.MemberName, cancellationToken));
        return EvaluateMemberAccess(target, memberName);
    }

    private async ValueTask<object?> EvaluateNullConditionalMemberAccessAsync(
        NullConditionalMemberAccessExpressionAst member,
        CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(member.Target, cancellationToken);
        // PowerShell's null gate and selected access branch evaluate the receiver separately.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6628-L6633
        return PowerShellWasmOutput.AsNull(target) is null ? null
            : EvaluateMemberAccess(await EvaluateExpressionAsync(member.Target, cancellationToken), member.MemberName);
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

        return IsSyntheticCountOrLength(memberName) ? 1 : null;
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
            if (legacyDictionary.Contains(memberName))
            {
                value = legacyDictionary[memberName];
                return true;
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
        if (!IsMemberEnumerable(target)) return false;
        var selected = new List<object?>();
        Collect(target, new HashSet<object>(ReferenceEqualityComparer.Instance));
        // Unlike a void method invocation, property enumeration with no results is an actual null value.
        value = selected.Count == 0 ? null : ToCapturedExpressionValue(selected);
        return true;

        // Behavioral guidance: missing scalar members are omitted, adapted custom-object members yield null,
        // and successful property results flatten once. Only a missing member recurses through its receiver.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L3180-L3227
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L5462-L5508
        void Collect(object receiver, HashSet<object> active)
        {
            EnterMemberEnumeration(receiver, active);
            try
            {
                foreach (var item in Enumerate(receiver))
                {
                    if (item is not null && TryGetDirectMemberAccess(item, memberName, out var itemValue))
                        selected.AddRange(PowerShellWasmOutput.Enumerate(itemValue));
                    else if (IsSyntheticCountOrLength(memberName))
                        selected.Add(PowerShellWasmOutput.AsNull(item) is null ? 0 : 1);
                    else if (item is PowerShellWasmCustomObject)
                        selected.Add(null);
                    else if (IsMemberEnumerable(item))
                        Collect(item!, active);
                }
            }
            finally { active.Remove(receiver); }
        }
    }

    private static bool IsSyntheticCountOrLength(string memberName) =>
        memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
        memberName.Equals("Length", StringComparison.OrdinalIgnoreCase);

    private static bool IsMemberEnumerable(object? target) =>
        target is IEnumerable or IEnumerator &&
        target is not (string or IDictionary or IReadOnlyDictionary<string, object?>);

    private static void EnterMemberEnumeration(object target, HashSet<object> active)
    {
        // Browser-safe guard: cyclic receivers must fail normally, while repeated noncyclic references still enumerate.
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (!active.Add(target)) throw new InvalidOperationException("Cannot enumerate a member through a cyclic collection.");
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
        object? receiver;
        string memberName;
        if (invocation.Target is NullConditionalMemberAccessExpressionAst nullConditionalMember)
        {
            receiver = await EvaluateExpressionAsync(nullConditionalMember.Target, cancellationToken);
            if (PowerShellWasmOutput.AsNull(receiver) is null) return null;
            receiver = await EvaluateExpressionAsync(nullConditionalMember.Target, cancellationToken);
            memberName = nullConditionalMember.MemberName;
        }
        else if (invocation.Target is MemberAccessExpressionAst member)
        {
            receiver = await EvaluateExpressionAsync(member.Target, cancellationToken);
            memberName = member.MemberName;
        }
        else if (invocation.Target is ComputedMemberAccessExpressionAst computedMember)
        {
            receiver = await EvaluateExpressionAsync(computedMember.Target, cancellationToken);
            if (computedMember.NullConditional && PowerShellWasmOutput.AsNull(receiver) is null) return null;
            if (computedMember.NullConditional) receiver = await EvaluateExpressionAsync(computedMember.Target, cancellationToken);
            memberName = ToInvariantString(await EvaluateExpressionAsync(computedMember.MemberName, cancellationToken));
        }
        else
        {
            var target = await EvaluateExpressionAsync(invocation.Target, cancellationToken);
            return InvokeMethodTarget(target, await EvaluateMethodArgumentsAsync(invocation.Arguments, cancellationToken));
        }

        // Arguments and computed names run once, including an unhandled intrinsic overload.
        // Null-conditional receivers use PowerShell's separate gate and access evaluations above.
        var arguments = await EvaluateMethodArgumentsAsync(invocation.Arguments, cancellationToken);
        if (IsIntrinsicCollectionMethod(memberName))
        {
            var intrinsic = await TryInvokeIntrinsicCollectionMethodAsync(receiver, memberName, arguments, cancellationToken);
            if (intrinsic.Handled) return intrinsic.Value;
        }

        return InvokeInstanceMethod(receiver, memberName, arguments);
    }

    private static object? InvokeInstanceMethod(object? receiver, string memberName, IReadOnlyList<object?> arguments)
    {
        receiver = PowerShellWasmOutput.AsNull(receiver);
        if (receiver is null) throw new InvalidOperationException("You cannot call a method on a null-valued expression.");
        if (PowerShellWasmDotNetBridge.TryGetInstanceMember(receiver, memberName, out var method) &&
            PowerShellWasmDotNetBridge.TryInvoke(method, arguments, out var value)) return value;
        if (TryInvokeEnumeratedMethods(receiver, memberName, arguments, out value)) return value;
        throw MissingInstanceMethod(memberName);
    }

    private static InvalidOperationException MissingInstanceMethod(string name) =>
        new($"Method '{name}' is not available on this value in the browser-safe runtime.");

    private static object? InvokeMethodTarget(object? target, IReadOnlyList<object?> arguments)
    {
        if (PowerShellWasmDotNetBridge.TryInvoke(target, arguments, out var value))
        {
            return value;
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
        if (arguments.Count == 0)
            throw new InvalidOperationException($"Method '{memberName}' requires an expression argument.");

        if (memberName.Equals("ForEach", StringComparison.OrdinalIgnoreCase))
        {
            // A real List<T>.ForEach(Action<T>) takes precedence over PowerShell's intrinsic fallback.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L6815-L6885
            if (target is List<object?> or List<string> or List<int> or List<long> or List<double> or
                List<decimal> or List<bool> or List<byte> or List<DateTime>)
                return (true, await InvokeListForEachAsync(target, arguments, cancellationToken));
            return (true, await InvokeIntrinsicForEachAsync(target, arguments, cancellationToken));
        }

        if (arguments.Count > 3)
            throw new InvalidOperationException("Method 'Where' accepts an expression, selection mode, and count.");
        var expression = PowerShellWasmOutput.AsNull(arguments[0]);
        if (expression is not null and not PowerShellWasmScriptBlock)
            throw new InvalidOperationException("The Where expression must be a script block.");
        var mode = arguments.Count > 1 ? PowerShellWasmDotNetBridge.ConvertWhereMode(arguments[1])
            : PowerShellWasmWhereMode.Default;
        var count = arguments.Count > 2 ? (int)CastValue("int", arguments[2])! : 0;
        if (count < 0) throw new InvalidOperationException("The Where count cannot be negative.");
        return (true, await InvokeIntrinsicWhereAsync(target, (PowerShellWasmScriptBlock?)expression,
            mode, count, cancellationToken));
    }

    private async ValueTask<object?> InvokeListForEachAsync(object target,
        IReadOnlyList<object?> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count != 1 || arguments[0] is not PowerShellWasmScriptBlock action)
            throw new InvalidOperationException("List.ForEach requires one script-block action.");
        try
        {
            // The list enumerator retains version checks, including a change made by the final callback.
            // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/List.cs#L638-L658
            foreach (var item in Enumerate(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action.InvokeCallbackAsync(PowerShellWasmOutput.NoOutput, [item], null,
                    PowerShellWasmScriptBlockInvocation.Delegate, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException and not ControlFlowException)
        {
            throw new InvalidOperationException($"Exception calling List.ForEach: {error.Message}", error);
        }
        return PowerShellWasmOutput.NoOutput;
    }

    // Behavioral guidance: EnumerableOps.ForEach returns a collection, keeps member results nested, and
    // uses caller-scope callbacks. Only the existing browser-safe member/type allowlist is exposed here.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L2875-L3134
    private async ValueTask<object?> InvokeIntrinsicForEachAsync(object? target,
        IReadOnlyList<object?> arguments, CancellationToken cancellationToken)
    {
        var expression = PowerShellWasmOutput.AsNull(arguments[0]);
        if (expression is null) throw new InvalidOperationException("The ForEach expression cannot be null.");
        var remaining = arguments.Skip(1).ToArray();
        if (expression is PowerShellWasmDotNetType type)
        {
            if (type.FullName.EndsWith("[]", StringComparison.Ordinal))
                return CastValue(type.FullName, Enumerate(target).ToArray());
            if (type.FullName.StartsWith("System.Collections.Generic.List[", StringComparison.OrdinalIgnoreCase))
            {
                var list = (IList)CastValue(type.FullName, Array.Empty<object?>())!;
                foreach (var item in Enumerate(target))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    list.Add(CoerceListItemValue(list, item));
                }
                return list;
            }
            return PowerShellWasmDotNetBridge.CreateIntrinsicTypedCollection(type.FullName, Enumerate(target), item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CastValue(type.FullName, item);
            });
        }

        var result = new Collection<object?>();
        if (expression is PowerShellWasmScriptBlock scriptBlock)
        {
            foreach (var item in Enumerate(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var output in await scriptBlock.InvokeCallbackAsync(item, remaining, null,
                    PowerShellWasmScriptBlockInvocation.Collection, cancellationToken)) result.Add(output);
            }
            return result;
        }

        var name = ToInvariantString(expression);
        var assignment = remaining.Length == 1 ? remaining[0] : remaining;
        foreach (var rawItem in Enumerate(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = PowerShellWasmOutput.AsNull(rawItem);
            if (item is Hashtable table)
            {
                if (remaining.Length == 0) result.Add(table[name]);
                else table[name] = PowerShellWasmOutput.AsNull(assignment);
                continue;
            }
            if (item is null)
            {
                if (remaining.Length != 0) throw MissingInstanceMethod(name);
                result.Add(null);
                continue;
            }
            if (name.Length == 0) throw new InvalidOperationException("A member name cannot be empty.");
            object? member;
            // Only Hashtable uses key lookup here; other dictionaries expose their actual CLR members.
            var found = item is PowerShellWasmCustomObject
                ? TryGetDirectMemberAccess(item, name, out member)
                : PowerShellWasmDotNetBridge.TryGetInstanceMember(item, name, out member) ||
                    TryGetCollectionProperty(item, name, out member);
            if (!found)
            {
                if (remaining.Length != 0) throw MissingInstanceMethod(name);
                result.Add(null);
            }
            else if (member is PowerShellWasmDotNetMethod)
                result.Add(PowerShellWasmOutput.AsNull(InvokeMethodTarget(member, remaining)));
            else if (remaining.Length == 0) result.Add(PowerShellWasmOutput.AsNull(member));
            else if (item is PowerShellWasmCustomObject) SetMemberValue(item, name, assignment);
            else throw new InvalidOperationException($"Member '{name}' cannot be assigned in this browser-safe runtime.");
        }
        return result;
    }

    // Where evaluates lazily: stopping modes leave an existing iterator at the last consumed item.
    // Split stops invoking the predicate once its match quota is reached, then drains the remainder.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L2658-L2866
    private async ValueTask<object?> InvokeIntrinsicWhereAsync(object? target, PowerShellWasmScriptBlock? predicate,
        PowerShellWasmWhereMode mode, int count, CancellationToken cancellationToken)
    {
        if (predicate is null) return SelectWithoutPredicate(target, mode, count, cancellationToken);
        if (mode is PowerShellWasmWhereMode.First or PowerShellWasmWhereMode.Last && count == 0) count = 1;
        var selected = new Collection<object?>();
        var rejected = new Collection<object?>();
        var skipping = mode == PowerShellWasmWhereMode.SkipUntil;
        foreach (var item in Enumerate(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mode == PowerShellWasmWhereMode.Split && count != 0 && selected.Count == count)
            {
                rejected.Add(item);
                continue;
            }
            var matches = mode == PowerShellWasmWhereMode.SkipUntil && !skipping ||
                ToBoolean(ToCapturedExpressionValue(await predicate.InvokeCallbackAsync(item, null, null,
                    PowerShellWasmScriptBlockInvocation.Collection, cancellationToken)));
            if (mode == PowerShellWasmWhereMode.Until)
            {
                if (matches) break;
                selected.Add(item);
            }
            else if (matches)
            {
                skipping = false;
                selected.Add(item);
                if (mode == PowerShellWasmWhereMode.Last && selected.Count > count) selected.RemoveAt(0);
            }
            else if (mode == PowerShellWasmWhereMode.Split) rejected.Add(item);

            if (count != 0 && selected.Count == count &&
                mode is not PowerShellWasmWhereMode.Last and not PowerShellWasmWhereMode.Split) break;
        }
        return mode == PowerShellWasmWhereMode.Split ? new object?[] { selected, rejected } : selected;
    }

    private static object? SelectWithoutPredicate(object? target, PowerShellWasmWhereMode mode, int count,
        CancellationToken cancellationToken)
    {
        if (mode == PowerShellWasmWhereMode.Default)
            throw new InvalidOperationException("Default Where selection requires a script block.");
        if (count == 0) count = 1;
        using var iterator = Enumerate(target).GetEnumerator();
        bool MoveNext()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return iterator.MoveNext();
        }
        if (mode == PowerShellWasmWhereMode.Last)
        {
            var tail = new Queue<object?>();
            while (MoveNext())
            {
                if (tail.Count == count) tail.Dequeue();
                tail.Enqueue(iterator.Current);
            }
            return tail.Count == 0 && count == 1 ? new object?[] { null } : tail.ToArray();
        }
        if (mode == PowerShellWasmWhereMode.SkipUntil)
        {
            for (var i = 0; i < count && MoveNext(); i++) { }
            var rest = new List<object?>();
            while (MoveNext()) rest.Add(iterator.Current);
            return rest.ToArray();
        }
        var head = new object?[count];
        for (var i = 0; i < count && MoveNext(); i++) head[i] = iterator.Current;
        if (mode != PowerShellWasmWhereMode.Split) return head;
        var remainder = new List<object?>();
        while (MoveNext()) remainder.Add(iterator.Current);
        return new object?[] { head, remainder.ToArray() };
    }

    private static bool TryInvokeEnumeratedMethods(
        object target,
        string memberName,
        IReadOnlyList<object?> arguments,
        out object? value)
    {
        value = null;
        if (!IsMemberEnumerable(target)) return false;
        var selected = new List<object?>();
        var foundMethod = false;
        Collect(target, new HashSet<object>(ReferenceEqualityComparer.Instance));
        if (!foundMethod) throw MissingInstanceMethod(memberName);
        value = ToCapturedExpressionValue(selected);
        return true;

        // Retain the receiver and method name rather than invoking previously enumerated property values.
        // Earlier side effects survive failure, but return values are buffered until the complete traversal succeeds.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L3230-L3325
        void Collect(object receiver, HashSet<object> active)
        {
            EnterMemberEnumeration(receiver, active);
            try
            {
                foreach (var item in Enumerate(receiver))
                {
                    if (PowerShellWasmOutput.AsNull(item) is null)
                        throw new InvalidOperationException("You cannot call a method on a null-valued expression.");
                    if (PowerShellWasmDotNetBridge.TryGetInstanceMember(item!, memberName, out var method) &&
                        PowerShellWasmDotNetBridge.TryInvoke(method, arguments, out var itemValue))
                    {
                        foundMethod = true;
                        selected.AddRange(PowerShellWasmOutput.Enumerate(itemValue));
                    }
                    else if (IsMemberEnumerable(item)) Collect(item!, active);
                    else throw MissingInstanceMethod(memberName);
                }
            }
            finally { active.Remove(receiver); }
        }
    }

    private async ValueTask<object?> EvaluateIndexAsync(IndexExpressionAst index, CancellationToken cancellationToken)
    {
        var target = await EvaluateExpressionAsync(index.Target, cancellationToken);
        var indexValue = await EvaluateExpressionAsync(index.Index, cancellationToken);
        return EvaluateIndex(target, indexValue, HasObjectIndexConstraint(index.Index),
            index.Index is ArrayExpressionAst { Items.Count: > 1 });
    }

    // Explicit object casts select OrderedDictionary's key overload even when the runtime value is an Int32.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6597-L6620
    private static bool HasObjectIndexConstraint(ExpressionAst index) => index switch
    {
        ParenthesizedExpressionAst parenthesized => HasObjectIndexConstraint(parenthesized.Expression),
        CastExpressionAst cast => NormalizeCastTypeName(cast.TypeName) is "object" or "psobject" or "system.management.automation.psobject",
        _ => false
    };

    // Native slicing preserves array identity and filters only AutomationNull, not actual null values.
    // Direct comma indexes on non-array indexers retain failed slots; one enumerable index filters them.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L4001-L4530
    private object? EvaluateIndex(object? target, object? indexValue, bool keyOnly = false, bool multipleIndexes = false)
    {
        target = PowerShellWasmOutput.AsNull(target);
        indexValue = PowerShellWasmOutput.AsNull(indexValue);
        if (target is null) throw new InvalidOperationException("Cannot index into a null array.");
        if (indexValue is null) throw new InvalidOperationException("Index operation failed; the array index evaluated to null.");
        if (TryGetDictionaryIndex(target, indexValue, keyOnly, multipleIndexes, out var dictionaryValue))
        {
            return dictionaryValue;
        }

        var slicing = IsOperatorCollection(indexValue);
        if (target is not string and not IList)
        {
            // Objects without an indexer are their own scalar element at 0/-1, including explicit enumerators.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/ArrayOps.cs#L271-L300
            return !slicing && (ValuesEqual(0, indexValue, false) || ValuesEqual(-1, indexValue, false))
                ? target : PowerShellWasmOutput.NoOutput;
        }

        var length = target is string text ? text.Length : ((IList)target).Count;
        object? Read(object? item)
        {
            item = PowerShellWasmOutput.AsNull(item);
            // An unavailable conversion is an error; a supported conversion that fails returns a missing slot.
            if (!CanConvertIndex(item)) throw new InvalidOperationException("The index cannot be converted to System.Int32.");
            try
            {
                var position = (int)ConvertNumericValue("int", item);
                if (position < 0) position += length;
                if (position >= 0 && position < length)
                    return target is string characters ? characters[position] : ((IList)target)[position];
            }
            catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException) { }
            return slicing ? PowerShellWasmOutput.NoOutput : null;
        }

        if (!slicing) return Read(indexValue);
        var selected = new List<object?>();
        foreach (var item in Enumerate(indexValue))
        {
            var value = Read(item);
            if (multipleIndexes && target is not Array || !PowerShellWasmOutput.IsNoOutput(value)) selected.Add(value);
        }
        return selected.ToArray();
    }

    private static bool CanConvertIndex(object? value) =>
        value is null or bool or char or string || IsNumericValue(value);

    private static bool TryGetDictionaryIndex(object? target, object? index, bool keyOnly, bool multipleIndexes, out object? value)
    {
        value = null;
        if (target is PowerShellWasmCustomObject) return false;
        var stringKeys = target is IDictionary<string, object?> or IReadOnlyDictionary<string, object?>;
        var dictionary = target as IDictionary;
        if (dictionary is null && target is IReadOnlyDictionary<string, object?> properties)
            dictionary = new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase);
        if (dictionary is null)
        {
            return false;
        }
        index = PowerShellWasmOutput.AsNull(index);
        if (index is null) throw new InvalidOperationException("The dictionary index cannot be null.");
        var slicing = IsOperatorCollection(index);
        object? Read(object? key)
        {
            key = PowerShellWasmOutput.AsNull(key);
            if (key is null) return null;
            if (stringKeys) key = ToInvariantString(key);
            if (!keyOnly && dictionary is OrderedDictionary ordered && key is int position)
            {
                // Native ordered reads permit negative indexes; writes use the strict BCL indexer.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L4157-L4193
                if (position < 0) position += ordered.Count;
                return position >= 0 && position < ordered.Count ? ordered[position]
                    : slicing ? PowerShellWasmOutput.NoOutput : null;
            }
            return dictionary.Contains(key) ? dictionary[key] : null;
        }
        value = slicing ? Enumerate(index).Select(Read)
            .Where(item => multipleIndexes || !PowerShellWasmOutput.IsNoOutput(item)).ToArray() : Read(index);
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
            PowerShellWasmUnaryOperator.BitwiseNot => BitwiseNot(value),
            PowerShellWasmUnaryOperator.Join => Join(value, string.Empty),
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
            value is not (IEnumerable or IEnumerator))
        {
            return false;
        }

        items = Enumerate(value);
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
            PowerShellWasmBinaryOperator.BitwiseAnd or PowerShellWasmBinaryOperator.BitwiseOr
                or PowerShellWasmBinaryOperator.BitwiseXor => BitwiseBinary(left, right, op),
            PowerShellWasmBinaryOperator.Join => Join(left, right),
            PowerShellWasmBinaryOperator.Split => SplitString(ToInvariantString(left), ToInvariantString(right), ignoreCase: true),
            PowerShellWasmBinaryOperator.CaseSensitiveSplit => SplitString(ToInvariantString(left), ToInvariantString(right), ignoreCase: false),
            PowerShellWasmBinaryOperator.ShiftLeft or PowerShellWasmBinaryOperator.ShiftRight => ShiftNumber(left, right, op),
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
            "char" => value is char,
            "scriptblock" or "system.management.automation.scriptblock" => value is PowerShellWasmScriptBlock,
            "bool" => value is bool,
            "switch" => value is bool,
            "int" => value is int,
            "long" => value is long,
            "double" => value is double,
            "decimal" => value is decimal,
            "byte" => value is byte,
            "sbyte" => value is sbyte,
            "short" => value is short,
            "ushort" => value is ushort,
            "uint" => value is uint,
            "ulong" => value is ulong,
            "float" => value is float,
            "bigint" => value is BigInteger,
            "object[]" => value is object?[],
            "string[]" => value.GetType() == typeof(string[]),
            "char[]" => value.GetType() == typeof(char[]),
            "bool[]" or "switch[]" => value.GetType() == typeof(bool[]),
            "int[]" => value.GetType() == typeof(int[]),
            "long[]" => value.GetType() == typeof(long[]),
            "double[]" => value.GetType() == typeof(double[]),
            "byte[]" => value.GetType() == typeof(byte[]),
            "sbyte[]" => value.GetType() == typeof(sbyte[]),
            "short[]" => value.GetType() == typeof(short[]),
            "ushort[]" => value.GetType() == typeof(ushort[]),
            "uint[]" => value.GetType() == typeof(uint[]),
            "ulong[]" => value.GetType() == typeof(ulong[]),
            "float[]" => value.GetType() == typeof(float[]),
            "decimal[]" => value.GetType() == typeof(decimal[]),
            "bigint[]" => value.GetType() == typeof(BigInteger[]),
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

            // Native dictionary addition preserves an ordered left operand and rejects duplicate keys.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L1407-L1461
            IDictionary result = leftDictionary is OrderedDictionary
                ? new OrderedDictionary(StringComparer.OrdinalIgnoreCase) : new PowerShellWasmHashtable();
            foreach (var dictionary in new[] { leftDictionary, rightDictionary })
            {
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (result.Contains(entry.Key))
                    {
                        throw new InvalidOperationException($"The key '{entry.Key}' already exists in the combined dictionary.");
                    }
                    result.Add(entry.Key, entry.Value);
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
            var count = (int)ConvertNumericValue("int", right);
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
                int[] values when values.GetType() == typeof(int[]) => RepeatArray(values, count),
                long[] values when values.GetType() == typeof(long[]) => RepeatArray(values, count),
                double[] values when values.GetType() == typeof(double[]) => RepeatArray(values, count),
                decimal[] values when values.GetType() == typeof(decimal[]) => RepeatArray(values, count),
                byte[] values when values.GetType() == typeof(byte[]) => RepeatArray(values, count),
                sbyte[] values when values.GetType() == typeof(sbyte[]) => RepeatArray(values, count),
                short[] values when values.GetType() == typeof(short[]) => RepeatArray(values, count),
                ushort[] values when values.GetType() == typeof(ushort[]) => RepeatArray(values, count),
                uint[] values when values.GetType() == typeof(uint[]) => RepeatArray(values, count),
                ulong[] values when values.GetType() == typeof(ulong[]) => RepeatArray(values, count),
                float[] values when values.GetType() == typeof(float[]) => RepeatArray(values, count),
                BigInteger[] values when values.GetType() == typeof(BigInteger[]) => RepeatArray(values, count),
                bool[] values when values.GetType() == typeof(bool[]) => RepeatArray(values, count),
                char[] values when values.GetType() == typeof(char[]) => RepeatArray(values, count),
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
        value is (IEnumerable or IEnumerator) and not string and not System.Collections.IDictionary
            and not IReadOnlyDictionary<string, object?>;

    // Unlike pipeline enumeration, concatenation retains explicit nulls and enumerates byte arrays.
    private static IEnumerable<object?> EnumerateOperatorOperand(object? value) =>
        IsOperatorCollection(value) ? Enumerate(value) : [value];

    private static object ToArithmeticNumber(object? value)
    {
        value = PowerShellWasmOutput.AsNull(value);
        if (value is null) return 0;
        if (value is bool boolean) return boolean ? 1 : 0;
        if (value is sbyte or byte or short or ushort) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (value is char character) return (int)character;
        if (value is float single) return (double)single;
        if (value is int or uint or long or ulong or double or decimal or BigInteger) return value;
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
        if (PowerShellWasmNumericLiteral.TryParse(token, out var literal)) return literal;

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

    // Behavioral guidance: numeric rank, signed/unsigned fallback, and explicit BigInteger overloads.
    // https://github.com/PowerShell/PowerShell/blob/v7.6.1/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L2149-L2238
    // Browser-safe subset: built-in numeric values only, without user-defined overloads or reflection.
    private static object NumericArithmetic(object? left, object? right, PowerShellWasmBinaryOperator op)
    {
        var lhs = ToArithmeticNumber(left);
        var rhs = ToArithmeticNumber(right);
        // Decimal's left-side overload takes precedence over BigInteger; other numeric left operands use BigInteger.
        if (lhs is BigInteger || rhs is BigInteger && lhs is not decimal)
        {
            var a = ToBigInteger(lhs);
            var b = ToBigInteger(rhs);
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
        if (lhs is decimal || rhs is decimal)
        {
            var a = ToNumericDecimal(lhs);
            var b = ToNumericDecimal(rhs);
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
            // UInt64 with a negative signed operand requires Decimal; UInt32 with one requires Int64.
            if ((lhs is ulong || rhs is ulong) && (IsNegativeSigned(lhs) || IsNegativeSigned(rhs)))
                return NumericArithmetic(ToNumericDecimal(lhs), ToNumericDecimal(rhs), op);
            var kind = lhs is ulong || rhs is ulong ? "ulong"
                : lhs is long || rhs is long ? "long"
                : lhs is uint || rhs is uint ? IsNegativeSigned(lhs) || IsNegativeSigned(rhs) ? "long" : "uint"
                : "int";
            var a = ToBigInteger(lhs);
            var b = ToBigInteger(rhs);
            if (op == PowerShellWasmBinaryOperator.Divide && a % b != BigInteger.Zero)
                return ToNumericDouble(lhs) / ToNumericDouble(rhs);
            var exact = op switch
            {
                PowerShellWasmBinaryOperator.Add => a + b,
                PowerShellWasmBinaryOperator.Subtract => a - b,
                PowerShellWasmBinaryOperator.Multiply => a * b,
                PowerShellWasmBinaryOperator.Divide => a / b,
                PowerShellWasmBinaryOperator.Remainder => a % b,
                _ => throw new InvalidOperationException($"Operator '{op}' is not numeric arithmetic.")
            };
            try
            {
                return kind switch
                {
                    "ulong" => (object)(ulong)exact, "long" => (long)exact,
                    "uint" => (uint)exact, _ => (int)exact
                };
            }
            catch (OverflowException)
            {
                // NumericOps uses floating arithmetic for 32-bit overflow. Long/ULong add/sub use Decimal,
                // while their multiplication uses BigInteger; its Double conversion has different rounding.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/NumericOps.cs
                if (kind is "int" or "uint")
                {
                    var x = ToNumericDouble(lhs);
                    var y = ToNumericDouble(rhs);
                    return op switch
                    {
                        PowerShellWasmBinaryOperator.Add => x + y,
                        PowerShellWasmBinaryOperator.Subtract => x - y,
                        PowerShellWasmBinaryOperator.Multiply => x * y,
                        PowerShellWasmBinaryOperator.Divide => x / y,
                        _ => x % y
                    };
                }
                if (op is PowerShellWasmBinaryOperator.Add or PowerShellWasmBinaryOperator.Subtract)
                    return (double)(decimal)exact;
                return (double)exact;
            }
        }

        var first = ToNumericDouble(lhs);
        var second = ToNumericDouble(rhs);
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

    private static bool IsNegativeSigned(object value) => value is int integer && integer < 0 || value is long wide && wide < 0;

    private static bool IsNumericValue(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or BigInteger;

    private static BigInteger ToBigInteger(object value) => value switch
    {
        BigInteger big => big,
        decimal number => new BigInteger(number),
        double number => new BigInteger(number),
        float number => new BigInteger(number),
        ulong number => new BigInteger(number),
        _ => new BigInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture))
    };

    private static double ToNumericDouble(object value) =>
        value is BigInteger big ? (double)big : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToNumericDecimal(object value) =>
        value is BigInteger big ? (decimal)big : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static object ToBitwiseInteger(object? value)
    {
        var number = ToArithmeticNumber(value);
        if (number is not (double or decimal)) return number;
        if (number is decimal money)
        {
            var rounded = Math.Round(money);
            if (rounded is >= int.MinValue and <= int.MaxValue) return (int)rounded;
            if (rounded is >= long.MinValue and <= long.MaxValue) return (long)rounded;
            return checked((ulong)rounded);
        }
        var floating = Math.Round((double)number);
        return floating is >= int.MinValue and <= int.MaxValue ? (object)(int)floating
            : Convert.ToInt64(floating, CultureInfo.InvariantCulture);
    }

    private static object BitwiseNot(object? value) => ToBitwiseInteger(value) switch
    {
        BigInteger big => (object)~big, ulong wide => ~wide, long wide => ~wide, uint unsigned => ~unsigned,
        int number => ~number, _ => throw new InvalidOperationException("The value cannot be used for a bitwise operation.")
    };

    private static object ShiftNumber(object? left, object? right, PowerShellWasmBinaryOperator op)
    {
        var value = ToBitwiseInteger(left);
        var shift = (int)ConvertNumericValue("int", right);
        var shiftLeft = op == PowerShellWasmBinaryOperator.ShiftLeft;
        return value switch
        {
            BigInteger big => (object)(shiftLeft ? big << shift : big >> shift),
            ulong wide => shiftLeft ? wide << shift : wide >> shift,
            long wide => shiftLeft ? wide << shift : wide >> shift,
            uint unsigned => shiftLeft ? unsigned << shift : unsigned >> shift,
            int number => shiftLeft ? number << shift : number >> shift,
            _ => throw new InvalidOperationException("The value cannot be shifted.")
        };
    }

    private static object BitwiseBinary(object? left, object? right, PowerShellWasmBinaryOperator op)
    {
        var lhs = ToArithmeticNumber(left);
        var rhs = ToArithmeticNumber(right);
        var big = lhs is BigInteger || rhs is BigInteger;
        var real = lhs is double or decimal || rhs is double or decimal;
        if (big && real) throw new InvalidOperationException("BigInteger bitwise operands must be integral.");
        string kind;
        if (real)
        {
            var signed = ToNumericDouble(lhs) < 0 || ToNumericDouble(rhs) < 0;
            kind = signed ? "long" : "ulong";
            lhs = ConvertNumericValue(kind, lhs);
            rhs = ConvertNumericValue(kind, rhs);
        }
        else kind = big ? "bigint" : lhs is ulong || rhs is ulong ? "ulong"
            : lhs is long || rhs is long ? "long" : lhs is uint || rhs is uint ? "uint" : "int";
        var a = ToBigInteger(lhs);
        var b = ToBigInteger(rhs);
        var result = op switch
        {
            PowerShellWasmBinaryOperator.BitwiseAnd => a & b,
            PowerShellWasmBinaryOperator.BitwiseOr => a | b,
            PowerShellWasmBinaryOperator.BitwiseXor => a ^ b,
            _ => throw new InvalidOperationException($"Operator '{op}' is not bitwise.")
        };
        return kind switch
        {
            "bigint" => (object)result, "ulong" => (ulong)(result & ulong.MaxValue), "long" => (long)result,
            "uint" => (uint)(result & uint.MaxValue), _ => (int)result
        };
    }

    private static object?[] Range(object? left, object? right)
    {
        if (left is BigInteger || right is BigInteger)
            throw new InvalidOperationException("BigInteger values cannot be range endpoints.");
        var start = (int)ConvertNumericValue("int", left);
        var end = (int)ConvertNumericValue("int", right);
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

    // Join uses IEnumerable, not IEnumerator; a plain iterator is stringified with $OFS and consumed as one operand.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/lang/parserutils.cs#L780-L794
    private string Join(object? left, object? right)
    {
        // Convert the separator first, even when the left operand is a plain iterator: conversion can consume it.
        var separator = ToExpandableString(right);
        return left is IEnumerator and not IEnumerable
            ? ToExpandableString(left)
            : string.Join(separator, EnumerateOperatorOperand(left).Select(ToInvariantString));
    }

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

    internal static bool IsComparisonNumber(object? value) => IsNumericValue(value);

    internal static double CompareNumbers(object left, object right)
    {
        // BigInteger comparison converts its right operand, so reversing decimal/floating operands can change the result.
        if (left is BigInteger big) return big.CompareTo(ToBigInteger(right));
        if (left is decimal || right is decimal)
        {
            return ToNumericDecimal(left).CompareTo(ToNumericDecimal(right));
        }

        if (left is float or double || right is float or double)
        {
            var first = ToNumericDouble(left);
            var second = ToNumericDouble(right);
            return double.IsNaN(first) || double.IsNaN(second) ? double.NaN : first.CompareTo(second);
        }

        return ToBigInteger(left).CompareTo(ToBigInteger(right));
    }

    private static bool ToComparisonBoolean(object? value)
    {
        if (IsComparisonNumber(value)) return ToBoolean(value);
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
            // Native $Matches uses integer keys for numbered groups and string keys for named groups.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/lang/parserutils.cs#L1193-L1214
            var matches = new Hashtable(StringComparer.CurrentCultureIgnoreCase);
            foreach (var name in regex.GetGroupNames())
            {
                if (match.Groups[name].Success)
                {
                    object key = int.TryParse(name, out var number) ? number : name;
                    matches[key] = match.Groups[name].Value;
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

        if (value is IEnumerator iterator)
        {
            while (iterator.MoveNext()) yield return iterator.Current;
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

        if (value is IEnumerator) return Enumerate(value).ToArray();

        return [value];
    }

    private static bool TryGetCollectionProperty(object? target, string memberName, out object? value)
    {
        value = null;
        // Only real collection properties belong here. Synthetic scalar Count/Length must not hide enumeration.
        if (target is Array array)
        {
            if (IsSyntheticCountOrLength(memberName)) value = array.Length;
            else if (memberName.Equals("LongLength", StringComparison.OrdinalIgnoreCase)) value = array.LongLength;
            else if (memberName.Equals("Rank", StringComparison.OrdinalIgnoreCase)) value = array.Rank;
            else return false;
            return true;
        }
        if (target is PowerShellWasmCustomObject) return false;
        if (target is ICollection collection && memberName.Equals("Count", StringComparison.OrdinalIgnoreCase))
        {
            value = collection.Count;
            return true;
        }
        if (target is IDictionary dictionary)
        {
            if (memberName.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Keys;
                return true;
            }
            if (memberName.Equals("Values", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Values;
                return true;
            }
        }

        if (memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) && target is string text)
        {
            value = text.Length;
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

    private static void SetMemberValue(object? target, string memberName, object? value)
    {
        if (TrySetDictionaryValue(target, memberName, value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Member '{memberName}' cannot be assigned in this browser-safe runtime.");
    }

    private static void SetIndexValue(object? target, object? index, object? value, bool keyOnly = false, bool multipleIndexes = false)
    {
        target = PowerShellWasmOutput.AsNull(target);
        index = PowerShellWasmOutput.AsNull(index);
        if (target is null) throw new InvalidOperationException("Cannot index into a null array.");
        if (index is null) throw new InvalidOperationException("Index operation failed; the array index evaluated to null.");
        if (multipleIndexes) throw new InvalidOperationException("Index assignment requires exactly one index value.");
        if (target is PowerShellWasmCustomObject)
            throw new InvalidOperationException("A custom object does not have an indexer; assign its named properties instead.");
        if (target is IDictionary dictionary)
        {
            index = PowerShellWasmOutput.AsNull(index);
            if (index is null) throw new InvalidOperationException("The dictionary index cannot be null.");
            if (target is IDictionary<string, object?>) index = ToInvariantString(index);
            if (!keyOnly && dictionary is OrderedDictionary ordered && index is int position) ordered[position] = value;
            else dictionary[index] = value;
            return;
        }
        if (TrySetDictionaryValue(target, ToInvariantString(index), value))
        {
            return;
        }

        if (target is System.Collections.IList list)
        {
            if (!CanConvertIndex(index)) throw new InvalidOperationException("The index cannot be converted to System.Int32.");
            var itemIndex = (int)ConvertNumericValue("int", index);
            if (itemIndex < 0)
            {
                itemIndex = list.Count + itemIndex;
            }

            if (itemIndex < 0 || itemIndex >= list.Count)
            {
                throw new InvalidOperationException($"Index {index} is outside the target collection.");
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
            string[] or List<string> or Collection<string> => "string",
            char[] or List<char> or Collection<char> when list is not Array || list.GetType() == typeof(char[]) => "char",
            int[] or List<int> or Collection<int> when list is not Array || list.GetType() == typeof(int[]) => "int",
            long[] or List<long> or Collection<long> when list is not Array || list.GetType() == typeof(long[]) => "long",
            double[] or List<double> or Collection<double> when list is not Array || list.GetType() == typeof(double[]) => "double",
            decimal[] or List<decimal> or Collection<decimal> when list is not Array || list.GetType() == typeof(decimal[]) => "decimal",
            bool[] or List<bool> or Collection<bool> when list is not Array || list.GetType() == typeof(bool[]) => "bool",
            byte[] or List<byte> or Collection<byte> when list is not Array || list.GetType() == typeof(byte[]) => "byte",
            sbyte[] or Collection<sbyte> when list is not Array || list.GetType() == typeof(sbyte[]) => "sbyte",
            short[] or Collection<short> when list is not Array || list.GetType() == typeof(short[]) => "short",
            ushort[] or Collection<ushort> when list is not Array || list.GetType() == typeof(ushort[]) => "ushort",
            uint[] or Collection<uint> when list is not Array || list.GetType() == typeof(uint[]) => "uint",
            ulong[] or Collection<ulong> when list is not Array || list.GetType() == typeof(ulong[]) => "ulong",
            float[] or Collection<float> when list is not Array || list.GetType() == typeof(float[]) => "float",
            BigInteger[] or Collection<BigInteger> when list is not Array || list.GetType() == typeof(BigInteger[]) => "bigint",
            DateTime[] or List<DateTime> or Collection<DateTime> => "datetime",
            Collection<DateTimeOffset> => "datetimeoffset",
            Collection<TimeSpan> => "timespan",
            Collection<PowerShellWasmWhereMode> => "System.Management.Automation.WhereOperatorSelectionMode",
            _ => null
        };

        return elementType is null ? value : CastValue(elementType, value);
    }

    private static bool TrySetDictionaryValue(object? target, string key, object? value)
    {
        value = PowerShellWasmOutput.AsNull(value);
        switch (target)
        {
            case IDictionary<string, object?> genericDictionary:
                genericDictionary[key] = value;
                return true;
            case System.Collections.IDictionary legacyDictionary:
                legacyDictionary[key] = value;
                return true;
            default:
                return false;
        }
    }

    private async ValueTask<string> ExpandStringAsync(StringExpressionAst text, CancellationToken cancellationToken)
    {
        var value = text.Value;
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length;)
        {
            if (value[i] != '$' || text.LiteralDollarOffsets?.Contains(i) == true)
            {
                builder.Append(value[i++]);
                continue;
            }

            if (TryReadSubexpression(value, i, out var script, out var nextIndex))
            {
                builder.Append(await EvaluateExpandableSubexpressionAsync(script, text.IsInCatch, text.IsInFinally, cancellationToken));
                i = nextIndex;
                continue;
            }

            if (TryReadBracedVariable(value, i, text.VariableBoundaryOffsets, out var bracedName, out nextIndex))
            {
                builder.Append(ExpandVariable(bracedName));
                i = nextIndex;
                continue;
            }

            if (TryReadVariable(value, i, text.VariableBoundaryOffsets, out var name, out nextIndex))
            {
                builder.Append(ExpandVariable(name));
                i = nextIndex;
                continue;
            }

            builder.Append(value[i++]);
        }

        return builder.ToString();
    }

    private async ValueTask<string> EvaluateExpandableSubexpressionAsync(
        string script, bool insideCatch, bool insideFinally, CancellationToken cancellationToken)
    {
        var captured = await CaptureExpressionOutputAsync(
            () => ExecuteScriptAsync(new PowerShellWasmParser().Parse(script, insideCatch, insideFinally), cancellationToken),
            cancellationToken);
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
        => PowerShellWasmParser.TryReadExpandableSubexpression(value, start, out script, out nextIndex);

    private static bool TryReadBracedVariable(string value, int start, IReadOnlySet<int>? boundaries,
        out string name, out int nextIndex)
    {
        name = string.Empty;
        nextIndex = start;
        return start + 1 < value.Length && value[start + 1] == '{' &&
            PowerShellWasmTokenizer.TryReadVariableName(value, start, out name, out nextIndex,
                inExpandableString: true, variableBoundaryOffsets: boundaries);
    }

    private static bool TryReadVariable(string value, int start, IReadOnlySet<int>? boundaries,
        out string name, out int nextIndex)
        => PowerShellWasmTokenizer.TryReadVariableName(value, start, out name, out nextIndex,
            inExpandableString: true, variableBoundaryOffsets: boundaries);

    // Native string conversion joins collections or consumes an iterator with $OFS, without recursively expanding elements.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshObject.cs#L1093-L1127
    private string ToExpandableString(object? value) =>
        value is PowerShellWasmCustomObject custom ? FormatThrowMessage(custom)
            : IsOperatorCollection(value)
            ? string.Join(executionContext.OutputFieldSeparator,
                Enumerate(value).Select(item => item is PowerShellWasmCustomObject ? string.Empty : ToInvariantString(item)))
            : ToInvariantString(value);

    private static bool ToBoolean(object? value) =>
        PowerShellWasmOutput.AsNull(value) switch
        {
            null => false,
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            sbyte signedByte => signedByte != 0,
            short small => small != 0,
            ushort small => small != 0,
            int intValue => intValue != 0,
            uint unsigned => unsigned != 0,
            long longValue => longValue != 0,
            ulong unsigned => unsigned != 0,
            float single => single != 0,
            double doubleValue => doubleValue != 0,
            decimal decimalValue => decimalValue != 0,
            BigInteger big => !big.IsZero,
            string stringValue => stringValue.Length > 0,
            char character => character != '\0',
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

    private static string ToInvariantString(object? value) => value switch
    {
        double number => number.ToString("G15", CultureInfo.InvariantCulture),
        float number => number.ToString("G7", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    // A value-producing multiple assignment emits its assigned slots, with a remainder array in the final slot.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L3524-L3531
    private object?[] AssignParallel(IReadOnlyList<string> variableNames, object? value)
    {
        var values = Enumerate(value).ToArray();
        var assigned = new object?[variableNames.Count];
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

            executionContext.AssignVariable(variableNames[i], assignedValue);
            assigned[i] = assignedValue;
        }
        return assigned;
    }

    private void IncrementVariable(string variableName, int delta)
    {
        var value = EvaluateVariableAssignmentTarget(variableName);
        executionContext.AssignVariable(variableName, IncrementNumber(value, delta));
    }

    private static object IncrementNumber(object? value, int delta)
    {
        value = PowerShellWasmOutput.AsNull(value);
        // Behavioral guidance: PSUnaryOperationBinder permits numeric values/null, not numeric strings or bools.
        // https://github.com/PowerShell/PowerShell/blob/v7.6.1/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L3338-L3367
        if (value is not null && (!IsNumericValue(value) || value is BigInteger))
        {
            throw new InvalidOperationException("The increment and decrement operators require a numeric value.");
        }

        return NumericArithmetic(value, delta, PowerShellWasmBinaryOperator.Add);
    }

    private abstract class ControlFlowException : Exception
    {
    }

    private sealed class StopUpstreamFlowException(PipelineStage stage, bool pipelineFailed) : ControlFlowException
    {
        internal PipelineStage Stage { get; } = stage;
        internal bool PipelineFailed { get; } = pipelineFailed;
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
