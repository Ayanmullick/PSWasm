namespace PSWasm.Commands;

internal sealed class GetTimeZoneCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
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

        await context.ExecutionContext.WriteOutputAsync(output, cancellationToken);
        return;
    }
}
