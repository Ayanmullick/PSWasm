using System.Globalization;

namespace PSWasm;

public sealed class PowerShellWasmCommandContext
{
    public PowerShellWasmCommandContext(
        PowerShellWasmExecutionContext executionContext,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<object?>? pipelineInput = null)
    {
        ExecutionContext = executionContext;
        Parameters = parameters;
        Arguments = arguments;
        PipelineInput = pipelineInput ?? [];
    }

    public PowerShellWasmExecutionContext ExecutionContext { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
    public IReadOnlyList<object?> Arguments { get; }
    public IReadOnlyList<object?> PipelineInput { get; }

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
