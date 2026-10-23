using PSWasm.Language;

namespace PSWasm;

internal enum PowerShellWasmScriptBlockInvocation { Host, Pipeline, PipelineBeginEnd, Collection, CalculatedProperty }

public sealed class PowerShellWasmScriptBlock
{
    private readonly Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask<IReadOnlyList<object?>>> _invoke;
    private readonly Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask<PowerShellWasmResult>> _invokeResult;
    private readonly Func<PowerShellWasmCommandContext, bool, CancellationToken, ValueTask> _invokeCommand;
    private readonly Func<object?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask> _invokeOutput;

    internal PowerShellWasmScriptBlock(
        Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask<IReadOnlyList<object?>>> invoke,
        Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask<PowerShellWasmResult>> invokeResult,
        IReadOnlyList<ParameterDeclarationAst> parameters,
        Func<PowerShellWasmCommandContext, bool, CancellationToken, ValueTask> invokeCommand,
        Func<object?, IReadOnlyDictionary<string, object?>?, PowerShellWasmScriptBlockInvocation, CancellationToken, ValueTask> invokeOutput,
        string? sourceText = null)
    {
        _invoke = invoke;
        _invokeResult = invokeResult;
        _invokeCommand = invokeCommand;
        _invokeOutput = invokeOutput;
        Parameters = parameters;
        SourceText = sourceText;
    }

    internal IReadOnlyList<ParameterDeclarationAst> Parameters { get; }
    internal string? SourceText { get; }

    internal ValueTask InvokeCommandAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken,
        bool dotSource = false) => _invokeCommand(context, dotSource, cancellationToken);

    internal ValueTask InvokeOutputAsync(
        object? input, IReadOnlyDictionary<string, object?>? variables, CancellationToken cancellationToken,
        PowerShellWasmScriptBlockInvocation invocation = PowerShellWasmScriptBlockInvocation.Pipeline) =>
        _invokeOutput(input, variables, invocation, cancellationToken);

    public ValueTask<IReadOnlyList<object?>> InvokeAsync(object? input = null, CancellationToken cancellationToken = default) =>
        _invoke(input, null, null, PowerShellWasmScriptBlockInvocation.Host, cancellationToken);

    public ValueTask<PowerShellWasmResult> InvokeResultAsync(
        object? input = null,
        IReadOnlyDictionary<string, object?>? variables = null,
        CancellationToken cancellationToken = default) =>
        _invokeResult(input, null, variables, PowerShellWasmScriptBlockInvocation.Host, cancellationToken);

    internal ValueTask<IReadOnlyList<object?>> InvokeCallbackAsync(
        object? input,
        IReadOnlyList<object?>? arguments,
        IReadOnlyDictionary<string, object?>? variables,
        PowerShellWasmScriptBlockInvocation invocation,
        CancellationToken cancellationToken) =>
        _invoke(input, arguments, variables, invocation, cancellationToken);
}
