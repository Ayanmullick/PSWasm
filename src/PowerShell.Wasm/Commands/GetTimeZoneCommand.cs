namespace PSWasm.Commands;

internal sealed class GetTimeZoneCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.Local;
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = local.Id,
            ["DisplayName"] = local.DisplayName,
            ["StandardName"] = local.StandardName,
            ["DaylightName"] = local.DaylightName,
            ["BaseUtcOffset"] = local.BaseUtcOffset.ToString()
        };

        context.ExecutionContext.WriteOutput(output);
        return ValueTask.CompletedTask;
    }
}
