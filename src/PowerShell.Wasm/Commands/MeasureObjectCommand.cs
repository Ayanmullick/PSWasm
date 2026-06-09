namespace PSWasm.Commands;

internal sealed class MeasureObjectCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var propertyNames = PowerShellWasmCommandUtilities.GetPropertyNames(context);
        var propertyName = propertyNames.Count > 0 ? propertyNames[0] : null;
        var values = PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput).ToArray();
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Count"] = values.Length
        };

        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            result["Property"] = propertyName;
        }

        var numericValues = values
            .Select(item => string.IsNullOrWhiteSpace(propertyName) ? item : PowerShellWasmCommandUtilities.GetMemberValue(item, propertyName))
            .Select(value => PowerShellWasmCommandUtilities.TryToNumber(value, out var number) ? (double?)number : null)
            .Where(static value => value.HasValue)
            .Select(static value => value.GetValueOrDefault())
            .ToArray();

        AddStatistics(result, context, numericValues);
        context.ExecutionContext.WriteOutput(result);
        return ValueTask.CompletedTask;
    }

    private static void AddStatistics(Dictionary<string, object?> result, PowerShellWasmCommandContext context, IReadOnlyList<double> values)
    {
        if (context.Parameters.ContainsKey("Sum"))
        {
            result["Sum"] = NormalizeNumber(values.Sum());
        }

        if (context.Parameters.ContainsKey("Average"))
        {
            result["Average"] = values.Count == 0 ? null : NormalizeNumber(values.Average());
        }

        if (context.Parameters.ContainsKey("Minimum"))
        {
            result["Minimum"] = values.Count == 0 ? null : NormalizeNumber(values.Min());
        }

        if (context.Parameters.ContainsKey("Maximum"))
        {
            result["Maximum"] = values.Count == 0 ? null : NormalizeNumber(values.Max());
        }
    }

    private static object NormalizeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000000001 && value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(value)
            : value;
}
