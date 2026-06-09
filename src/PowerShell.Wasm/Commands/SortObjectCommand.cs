namespace PSWasm.Commands;

internal sealed class SortObjectCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var propertyNames = PowerShellWasmCommandUtilities.GetPropertyNames(context);
        var descending = context.Parameters.TryGetValue("Descending", out var descendingValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(descendingValue);
        var unique = context.Parameters.TryGetValue("Unique", out var uniqueValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(uniqueValue);

        var comparer = new SortObjectComparer(propertyNames, descending);
        var sorted = PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput).Order(comparer);
        object? previous = null;
        var hasPrevious = false;

        foreach (var item in sorted)
        {
            if (unique && hasPrevious && comparer.Compare(previous, item) == 0)
            {
                continue;
            }

            context.ExecutionContext.WriteOutput(item);
            previous = item;
            hasPrevious = true;
        }

        return ValueTask.CompletedTask;
    }

    private sealed class SortObjectComparer(IReadOnlyList<string> propertyNames, bool descending) : IComparer<object?>
    {
        public int Compare(object? x, object? y)
        {
            var result = propertyNames.Count == 0 ? CompareSingle(x, y) : CompareProperties(x, y);
            return descending ? -result : result;
        }

        private static int CompareSingle(object? x, object? y) =>
            PowerShellWasmCommandUtilities.CompareValues(x, y);

        private int CompareProperties(object? x, object? y)
        {
            foreach (var propertyName in propertyNames)
            {
                var result = PowerShellWasmCommandUtilities.CompareValues(
                    PowerShellWasmCommandUtilities.GetMemberValue(x, propertyName),
                    PowerShellWasmCommandUtilities.GetMemberValue(y, propertyName));
                if (result != 0)
                {
                    return result;
                }
            }

            return 0;
        }
    }
}
