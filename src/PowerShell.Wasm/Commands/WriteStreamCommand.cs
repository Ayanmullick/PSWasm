using System.Globalization;

namespace PSWasm.Commands;

internal sealed class WriteStreamCommand(string streamName, params string[] parameterNames) : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var message = GetMessage(context);

        if (streamName.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            context.ExecutionContext.WriteOutput(message);
        }
        else if (streamName.Equals("Progress", StringComparison.OrdinalIgnoreCase))
        {
            context.ExecutionContext.WriteStream(streamName, FormatProgress(context, message));
        }
        else
        {
            context.ExecutionContext.WriteStream(streamName, message);
        }

        return ValueTask.CompletedTask;
    }

    private string GetMessage(PowerShellWasmCommandContext context)
    {
        foreach (var name in parameterNames)
        {
            if (context.Parameters.TryGetValue(name, out var value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        var input = context.Arguments.Count > 0 ? context.Arguments : context.PipelineInput;
        return string.Join(" ", input.Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture)));
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
