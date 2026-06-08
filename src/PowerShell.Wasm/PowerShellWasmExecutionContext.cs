namespace PSWasm;

public sealed class PowerShellWasmExecutionContext
{
    private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environment;
    private readonly List<object?> _output = [];
    private readonly Stack<List<object?>> _outputCaptures = [];

    public PowerShellWasmExecutionContext(IDictionary<string, string>? environment = null)
    {
        _environment = environment is null ? new(StringComparer.OrdinalIgnoreCase) : new(environment, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Output => _output.Select(FormatOutput).ToArray();

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

        ActiveOutput.Add(value);
    }

    internal IDisposable CaptureOutput(List<object?> output)
    {
        _outputCaptures.Push(output);
        return new OutputCapture(this, output);
    }

    private List<object?> ActiveOutput =>
        _outputCaptures.TryPeek(out var output) ? output : _output;

    private void ReleaseOutputCapture(List<object?> output)
    {
        if (!_outputCaptures.TryPop(out var current) || !ReferenceEquals(current, output))
        {
            throw new InvalidOperationException("Output capture stack is unbalanced.");
        }
    }

    private static string FormatOutput(object? value) =>
        value switch
        {
            null => string.Empty,
            Dictionary<string, object?> hashtable => "@{" + string.Join("; ", hashtable.Select(static item => $"{item.Key}={FormatOutput(item.Value)}")) + "}",
            object?[] array => string.Join(Environment.NewLine, array.Select(FormatOutput)),
            _ => value.ToString() ?? string.Empty
        };

    private sealed class OutputCapture(PowerShellWasmExecutionContext context, List<object?> output) : IDisposable
    {
        public void Dispose() =>
            context.ReleaseOutputCapture(output);
    }
}
