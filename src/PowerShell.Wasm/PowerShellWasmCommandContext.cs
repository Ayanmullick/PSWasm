using System.Globalization;

namespace PSWasm;

public sealed class PowerShellWasmCommandContext(
    PowerShellWasmExecutionContext executionContext,
    IReadOnlyDictionary<string, object?> parameters,
    IReadOnlyList<object?> arguments)
{
    public PowerShellWasmExecutionContext ExecutionContext { get; } = executionContext;
    public IReadOnlyDictionary<string, object?> Parameters { get; } = parameters;
    public IReadOnlyList<object?> Arguments { get; } = arguments;

    public string? GetString(string name)
    {
        return Parameters.TryGetValue(name, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    public int GetInt32(string name, int fallback)
    {
        var value = GetString(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }
}
