namespace PSWasm.Commands;

internal sealed class ForEachObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var begin = GetParameterScriptBlock(context, "Begin");
        var process = GetParameterScriptBlock(context, "Process") ?? context.Arguments.OfType<PowerShellWasmScriptBlock>().FirstOrDefault();
        var end = GetParameterScriptBlock(context, "End");
        if (begin is null && process is null && end is null)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
            {
                context.ExecutionContext.WriteOutput(item);
            }

            return;
        }

        if (begin is not null)
        {
            await WriteScriptBlockOutputAsync(context, begin, null, cancellationToken);
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
        {
            if (process is not null)
            {
                await WriteScriptBlockOutputAsync(context, process, item, cancellationToken);
            }
        }

        if (end is not null)
        {
            await WriteScriptBlockOutputAsync(context, end, null, cancellationToken);
        }
    }

    private static async ValueTask WriteScriptBlockOutputAsync(
        PowerShellWasmCommandContext context,
        PowerShellWasmScriptBlock scriptBlock,
        object? input,
        CancellationToken cancellationToken)
    {
        foreach (var output in await scriptBlock.InvokeAsync(input, cancellationToken))
        {
            context.ExecutionContext.WriteOutput(output);
        }
    }

    private static PowerShellWasmScriptBlock? GetParameterScriptBlock(PowerShellWasmCommandContext context, string parameterName) =>
        context.Parameters.TryGetValue(parameterName, out var value) && value is PowerShellWasmScriptBlock scriptBlock ? scriptBlock : null;
}
