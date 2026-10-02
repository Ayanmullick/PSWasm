using PSWasm.Language;

namespace PSWasm;

public sealed class PowerShellWasmScriptBlock
{
    private readonly Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<object?>>> _invoke;
    private readonly Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<PowerShellWasmResult>> _invokeResult;
    private readonly Func<PowerShellWasmCommandContext, CancellationToken, ValueTask> _invokeCommand;

    internal PowerShellWasmScriptBlock(
        Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<object?>>> invoke,
        Func<object?, IReadOnlyList<object?>?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<PowerShellWasmResult>> invokeResult,
        IReadOnlyList<ParameterDeclarationAst> parameters,
        Func<PowerShellWasmCommandContext, CancellationToken, ValueTask> invokeCommand)
    {
        _invoke = invoke;
        _invokeResult = invokeResult;
        _invokeCommand = invokeCommand;
        Parameters = parameters;
    }

    internal IReadOnlyList<ParameterDeclarationAst> Parameters { get; }

    internal ValueTask InvokeCommandAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken) =>
        _invokeCommand(context, cancellationToken);

    public ValueTask<IReadOnlyList<object?>> InvokeAsync(object? input = null, CancellationToken cancellationToken = default) =>
        _invoke(input, null, null, cancellationToken);

    public ValueTask<PowerShellWasmResult> InvokeResultAsync(
        object? input = null,
        IReadOnlyDictionary<string, object?>? variables = null,
        CancellationToken cancellationToken = default) =>
        _invokeResult(input, null, variables, cancellationToken);

    internal ValueTask<IReadOnlyList<object?>> InvokeAsync(
        object? input,
        IReadOnlyList<object?>? arguments,
        IReadOnlyDictionary<string, object?>? variables,
        CancellationToken cancellationToken) =>
        _invoke(input, arguments, variables, cancellationToken);
}
