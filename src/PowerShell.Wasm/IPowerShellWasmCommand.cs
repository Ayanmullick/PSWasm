namespace PSWasm;

public interface IPowerShellWasmCommand
{
    IReadOnlyCollection<string> SwitchParameters => [];

    ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken);
}
