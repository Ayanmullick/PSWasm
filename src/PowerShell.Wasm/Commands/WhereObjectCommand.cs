namespace PSWasm.Commands;

internal sealed class WhereObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var scriptBlock = PowerShellWasmCommandUtilities.GetScriptBlock(context, "FilterScript", "Process");
        if (scriptBlock is null)
        {
            return;
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
        {
            var filterOutput = await scriptBlock.InvokeAsync(item, cancellationToken);
            if (filterOutput.Any(PowerShellWasmCommandUtilities.ToBoolean))
            {
                context.ExecutionContext.WriteOutput(item);
            }
        }
    }
}
