namespace PSWasm.Commands;

internal sealed class OutStringCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var stream = context.Parameters.TryGetValue("Stream", out var streamValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(streamValue);
        var input = GetInput(context).Select(PowerShellWasmCommandUtilities.FormatValue).ToArray();

        if (stream)
        {
            foreach (var item in input)
            {
                context.ExecutionContext.WriteOutput(item);
            }
        }
        else
        {
            context.ExecutionContext.WriteOutput(string.Join(Environment.NewLine, input));
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<object?> GetInput(PowerShellWasmCommandContext context)
    {
        if (context.PipelineInput.Count > 0)
        {
            return PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput);
        }

        return context.Arguments;
    }
}
