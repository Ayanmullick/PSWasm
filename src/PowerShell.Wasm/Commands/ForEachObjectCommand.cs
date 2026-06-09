namespace PSWasm.Commands;

internal sealed class ForEachObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var scriptBlock = PowerShellWasmCommandUtilities.GetScriptBlock(context, "Process");
        if (scriptBlock is null)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
            {
                context.ExecutionContext.WriteOutput(item);
            }

            return;
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
        {
            foreach (var output in await scriptBlock.InvokeAsync(item, cancellationToken))
            {
                context.ExecutionContext.WriteOutput(output);
            }
        }
    }
}
