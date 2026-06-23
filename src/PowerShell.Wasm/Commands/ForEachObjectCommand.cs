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
            foreach (var item in context.PipelineInput)
            {
                context.ExecutionContext.WriteOutput(item);
            }

            return;
        }

        if (begin is not null)
        {
            await WriteScriptBlockOutputAsync(context, begin, null, NoPipelineVariables, cancellationToken);
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumeratePipelineInput(context.PipelineInput))
        {
            if (process is not null)
            {
                await WriteScriptBlockOutputAsync(context, process, item.Value, item.Variables, cancellationToken);
            }
        }

        if (end is not null)
        {
            await WriteScriptBlockOutputAsync(context, end, null, NoPipelineVariables, cancellationToken);
        }
    }

    private static async ValueTask WriteScriptBlockOutputAsync(
        PowerShellWasmCommandContext context,
        PowerShellWasmScriptBlock scriptBlock,
        object? input,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        foreach (var output in await scriptBlock.InvokeAsync(input, null, variables, cancellationToken))
        {
            context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(output, variables));
        }
    }

    private static PowerShellWasmScriptBlock? GetParameterScriptBlock(PowerShellWasmCommandContext context, string parameterName) =>
        context.Parameters.TryGetValue(parameterName, out var value) && value is PowerShellWasmScriptBlock scriptBlock ? scriptBlock : null;

    private static readonly IReadOnlyDictionary<string, object?> NoPipelineVariables =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
