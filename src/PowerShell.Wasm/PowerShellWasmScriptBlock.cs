namespace PSWasm;

public sealed class PowerShellWasmScriptBlock
{
    private readonly Func<object?, CancellationToken, ValueTask<IReadOnlyList<object?>>> _invoke;

    internal PowerShellWasmScriptBlock(Func<object?, CancellationToken, ValueTask<IReadOnlyList<object?>>> invoke)
    {
        _invoke = invoke;
    }

    public ValueTask<IReadOnlyList<object?>> InvokeAsync(object? input = null, CancellationToken cancellationToken = default) =>
        _invoke(input, cancellationToken);
}
