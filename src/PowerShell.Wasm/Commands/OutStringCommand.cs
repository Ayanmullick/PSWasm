namespace PSWasm.Commands;

internal sealed class OutStringCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Stream"))
            ? new PowerShellWasmPerRecordProcessor(this, context, hasPipelineInput)
            : new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);

    public IReadOnlyCollection<string> SwitchParameters => ["Stream"];

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var stream = context.Parameters.TryGetValue("Stream", out var streamValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(streamValue);
        var input = GetInput(context).Where(static item => item is not null)
            .Select(PowerShellWasmCommandUtilities.FormatValue).ToArray();

        if (stream)
        {
            foreach (var item in input)
            {
                await context.ExecutionContext.WriteOutputAsync(item, cancellationToken);
            }
        }
        else
        {
            await context.ExecutionContext.WriteOutputAsync(string.Join(Environment.NewLine, input), cancellationToken);
        }

        return;
    }

    private static IEnumerable<object?> GetInput(PowerShellWasmCommandContext context)
    {
        if (context.PipelineInput.Count > 0)
        {
            return PowerShellWasmCommandUtilities.EnumeratePipelineValues(context.PipelineInput);
        }

        return context.Arguments;
    }
}
