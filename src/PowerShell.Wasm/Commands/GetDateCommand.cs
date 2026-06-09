using System.Globalization;

namespace PSWasm.Commands;

internal sealed class GetDateCommand(bool timeOnly = false) : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var useUtc = context.Parameters.TryGetValue("Utc", out var utc) && IsTruthy(utc);
        var now = useUtc ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        var format = context.GetString("Format");

        object output = timeOnly
            ? now.ToString(format ?? "T", CultureInfo.InvariantCulture)
            : format is null ? now : now.ToString(format, CultureInfo.InvariantCulture);

        context.ExecutionContext.WriteOutput(output);
        return ValueTask.CompletedTask;
    }

    private static bool IsTruthy(object? value) =>
        value switch
        {
            null => false,
            bool boolValue => boolValue,
            string stringValue => stringValue.Length > 0,
            _ => true
        };
}
