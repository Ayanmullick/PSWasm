namespace PSWasm;

public sealed class PowerShellWasmExecutionContext
{
    private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environment;
    private readonly List<string> _output = [];

    public PowerShellWasmExecutionContext(IDictionary<string, string>? environment = null)
    {
        _environment = environment is null ? new(StringComparer.OrdinalIgnoreCase) : new(environment, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Output => _output;

    public string? GetEnvironmentVariable(string name) =>
        _environment.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

    public object? GetVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : null;

    public void SetVariable(string name, object? value) =>
        _variables[name] = value;

    public void WriteOutput(object? value)
    {
        if (value is null)
        {
            return;
        }

        _output.Add(value.ToString() ?? string.Empty);
    }
}
