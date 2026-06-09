namespace PSWasm.Commands;

internal sealed class GroupObjectCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var propertyNames = PowerShellWasmCommandUtilities.GetPropertyNames(context);
        var groups = new Dictionary<string, GroupBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput))
        {
            var values = GetGroupValues(item, propertyNames);
            var name = string.Join(", ", values.Select(PowerShellWasmCommandUtilities.FormatValue));
            if (!groups.TryGetValue(name, out var group))
            {
                group = new(name, values);
                groups[name] = group;
            }

            group.Items.Add(item);
        }

        foreach (var group in groups.Values)
        {
            context.ExecutionContext.WriteOutput(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Count"] = group.Items.Count,
                ["Name"] = group.Name,
                ["Group"] = group.Items.ToArray(),
                ["Values"] = group.Values
            });
        }

        return ValueTask.CompletedTask;
    }

    private static object?[] GetGroupValues(object? item, IReadOnlyList<string> propertyNames)
    {
        if (propertyNames.Count == 0)
        {
            return [item];
        }

        return propertyNames
            .Select(propertyName => PowerShellWasmCommandUtilities.GetMemberValue(item, propertyName))
            .ToArray();
    }

    private sealed record GroupBucket(string Name, object?[] Values)
    {
        public List<object?> Items { get; } = [];
    }
}
