namespace PSWasm;

public sealed class PowerShellWasmScriptBlock
{
    private readonly Func<object?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<object?>>> _invoke;
    private readonly Func<object?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<PowerShellWasmResult>> _invokeResult;

    internal PowerShellWasmScriptBlock(
        Func<object?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<object?>>> invoke,
        Func<object?, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<PowerShellWasmResult>> invokeResult)
    {
        _invoke = invoke;
        _invokeResult = invokeResult;
    }

    public ValueTask<IReadOnlyList<object?>> InvokeAsync(object? input = null, CancellationToken cancellationToken = default) =>
        _invoke(input, null, cancellationToken);

    public ValueTask<PowerShellWasmResult> InvokeResultAsync(
        object? input = null,
        IReadOnlyDictionary<string, object?>? variables = null,
        CancellationToken cancellationToken = default) =>
        _invokeResult(input, variables, cancellationToken);

    internal ValueTask<IReadOnlyList<object?>> InvokeAsync(
        object? input,
        IReadOnlyDictionary<string, object?>? variables,
        CancellationToken cancellationToken) =>
        _invoke(input, variables, cancellationToken);
}
