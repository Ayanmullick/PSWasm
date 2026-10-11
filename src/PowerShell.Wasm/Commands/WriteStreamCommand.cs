namespace PSWasm.Commands;

internal sealed class WriteStreamCommand(string streamName, params string[] parameterNames) : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        context.Arguments.Count == 0 && !parameterNames.Any(context.Parameters.ContainsKey)
            ? new PowerShellWasmPerRecordProcessor(this, context, hasPipelineInput)
            : new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var message = GetMessage(context);

        if (streamName.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            await context.ExecutionContext.WriteHostAsync(message, cancellationToken);
        }
        else if (streamName.Equals("Progress", StringComparison.OrdinalIgnoreCase))
        {
            await context.ExecutionContext.WriteStreamAsync(streamName, FormatProgress(context, message), cancellationToken);
        }
        else
        {
            await context.ExecutionContext.WriteStreamAsync(streamName, message, cancellationToken);
        }

        return;
    }

    private string GetMessage(PowerShellWasmCommandContext context)
    {
        foreach (var name in parameterNames)
        {
            if (context.Parameters.TryGetValue(name, out var value))
            {
                return PowerShellWasmCommandUtilities.ToInvariantString(value);
            }
        }

        var input = context.Arguments.Count > 0 ? context.Arguments : context.PipelineInput;
        return string.Join(" ", input.Select(PowerShellWasmCommandUtilities.ToInvariantString));
    }

    private static string FormatProgress(PowerShellWasmCommandContext context, string fallback)
    {
        var activity = context.GetString("Activity") ?? fallback;
        var status = context.GetString("Status");
        var percent = context.GetString("PercentComplete");
        var parts = new[] { activity, status, percent is null ? null : $"{percent}%" };
        return string.Join(" - ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }
}
