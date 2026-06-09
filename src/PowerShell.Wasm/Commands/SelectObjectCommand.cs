namespace PSWasm.Commands;

internal sealed class SelectObjectCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput).ToArray();
        var selected = ApplyWindow(input, context);
        var expandProperty = context.GetString("ExpandProperty");
        var propertyNames = PowerShellWasmCommandUtilities.GetPropertyNames(context);

        foreach (var item in selected)
        {
            if (!string.IsNullOrWhiteSpace(expandProperty))
            {
                context.ExecutionContext.WriteOutput(PowerShellWasmCommandUtilities.GetMemberValue(item, expandProperty));
                continue;
            }

            if (propertyNames.Count > 0)
            {
                context.ExecutionContext.WriteOutput(SelectProperties(item, propertyNames));
                continue;
            }

            context.ExecutionContext.WriteOutput(item);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<object?> ApplyWindow(IReadOnlyList<object?> input, PowerShellWasmCommandContext context)
    {
        var skip = Math.Max(0, context.GetInt32("Skip", 0));
        var first = context.Parameters.ContainsKey("First") ? Math.Max(0, context.GetInt32("First", input.Count)) : (int?)null;
        var last = context.Parameters.ContainsKey("Last") ? Math.Max(0, context.GetInt32("Last", input.Count)) : (int?)null;

        var selected = input.Skip(skip);
        if (last is not null)
        {
            selected = selected.TakeLast(last.Value);
        }

        if (first is not null)
        {
            selected = selected.Take(first.Value);
        }

        return selected;
    }

    private static Dictionary<string, object?> SelectProperties(object? item, IReadOnlyList<string> propertyNames)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in propertyNames)
        {
            result[propertyName] = PowerShellWasmCommandUtilities.GetMemberValue(item, propertyName);
        }

        return result;
    }
}
