namespace PSWasm;

public interface IPowerShellWasmCommand
{
    ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken);
}
