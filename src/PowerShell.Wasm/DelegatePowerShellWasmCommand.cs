namespace PSWasm;

public sealed class DelegatePowerShellWasmCommand(Func<PowerShellWasmCommandContext, CancellationToken, ValueTask> handler)
    : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken) =>
        handler(context, cancellationToken);
}
