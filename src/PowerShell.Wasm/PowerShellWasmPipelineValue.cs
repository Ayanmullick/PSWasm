namespace PSWasm;

internal sealed record PowerShellWasmPipelineValue(
    object? Value,
    IReadOnlyDictionary<string, object?> Variables)
{
    public static object? Wrap(object? value, IReadOnlyDictionary<string, object?> variables)
    {
        if (value is null || variables.Count == 0)
        {
            return value;
        }

        var merged = value is PowerShellWasmPipelineValue existing
            ? new Dictionary<string, object?>(existing.Variables, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            merged[variable.Key] = variable.Value;
        }

        return new PowerShellWasmPipelineValue(Unwrap(value), merged);
    }

    public static object? Unwrap(object? value) =>
        value is PowerShellWasmPipelineValue pipelineValue ? pipelineValue.Value : value;

    public static IReadOnlyDictionary<string, object?> GetVariables(object? value) =>
        value is PowerShellWasmPipelineValue pipelineValue
            ? pipelineValue.Variables
            : EmptyVariables;

    private static readonly IReadOnlyDictionary<string, object?> EmptyVariables =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}

internal readonly record struct PowerShellWasmPipelineInputItem(
    object? Value,
    IReadOnlyDictionary<string, object?> Variables);
