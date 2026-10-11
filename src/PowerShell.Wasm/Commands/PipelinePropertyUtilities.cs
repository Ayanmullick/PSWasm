namespace PSWasm.Commands;

internal static class PipelinePropertyUtilities
{
    public static IReadOnlyList<object?> GetExpressions(PowerShellWasmCommandContext context)
    {
        var expressions = new List<object?>();
        if (context.Parameters.TryGetValue("Property", out var property))
            expressions.AddRange(PowerShellWasmCommandUtilities.EnumerateInput([property]));
        foreach (var argument in context.Arguments)
            expressions.AddRange(PowerShellWasmCommandUtilities.EnumerateInput([argument]));
        return expressions;
    }

    public static async ValueTask<object?[]> EvaluateAsync(
        PowerShellWasmPipelineInputItem item, IReadOnlyList<object?> expressions, CancellationToken cancellationToken)
    {
        if (expressions.Count == 0) return [item.Value];
        var values = new object?[expressions.Count];
        for (var index = 0; index < expressions.Count; index++)
        {
            if (expressions[index] is PowerShellWasmScriptBlock scriptBlock)
            {
                var result = await scriptBlock.InvokeAsync(item.Value, null, item.Variables, cancellationToken);
                values[index] = result.Count switch { 0 => null, 1 => result[0], _ => result.ToArray() };
            }
            else
            {
                var name = PowerShellWasmCommandUtilities.ToInvariantString(expressions[index]);
                values[index] = PowerShellWasmCommandUtilities.GetMemberValue(item.Value, name);
            }
        }
        return values;
    }

    public static int Compare(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            var comparison = PowerShellWasmCommandUtilities.CompareValues(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }
}