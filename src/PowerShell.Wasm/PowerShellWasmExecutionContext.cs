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

    public IReadOnlyList<PowerShellWasmOutputRecord> Records => _output.Select(FormatRecord).ToArray();
    public IReadOnlyList<string> Output => Records.Select(FormatRecordLine).ToArray();

    public string? GetEnvironmentVariable(string name) =>
        _environment.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

    public object? GetVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : null;

    public void SetVariable(string name, object? value) =>
        _variables[name] = value;

    internal IDisposable WithPipelineItem(object? value)
    {
        var hadUnderscore = _variables.TryGetValue("_", out var underscore);
        var hadPSItem = _variables.TryGetValue("PSItem", out var psItem);
        _variables["_"] = value;
        _variables["PSItem"] = value;
        return new PipelineItemScope(this, hadUnderscore, underscore, hadPSItem, psItem);
    }

    public void WriteOutput(object? value)
    {
        if (value is null)
        {
            return;
        }

        ActiveOutput.Add(value);
    }

    public void WriteStream(string streamName, object? value)
    {
        if (value is null)
        {
            return;
        }

        ActiveOutput.Add(new PowerShellWasmStreamRecord(streamName, value));
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

    private void RestorePipelineItem(bool hadUnderscore, object? underscore, bool hadPSItem, object? psItem)
    {
        RestoreVariable("_", hadUnderscore, underscore);
        RestoreVariable("PSItem", hadPSItem, psItem);
    }

    private void RestoreVariable(string name, bool hadValue, object? value)
    {
        if (hadValue)
        {
            _variables[name] = value;
        }
        else
        {
            _variables.Remove(name);
        }
    }

    private static PowerShellWasmOutputRecord FormatRecord(object? value) =>
        value switch
        {
            PowerShellWasmStreamRecord stream => new(stream.StreamName, FormatOutput(stream.Value)),
            _ => new("Output", FormatOutput(value))
        };

    private static string FormatRecordLine(PowerShellWasmOutputRecord record) =>
        record.Stream.Equals("Output", StringComparison.OrdinalIgnoreCase)
            ? record.Text
            : $"[{record.Stream}] {record.Text}";

    private static string FormatOutput(object? value) =>
        value switch
        {
            null => string.Empty,
            PowerShellWasmStreamRecord stream => FormatOutput(stream.Value),
            Dictionary<string, object?> hashtable => "@{" + string.Join("; ", hashtable.Select(static item => $"{item.Key}={FormatOutput(item.Value)}")) + "}",
            object?[] array => string.Join(Environment.NewLine, array.Select(FormatOutput)),
            _ => value.ToString() ?? string.Empty
        };

    private sealed record PowerShellWasmStreamRecord(string StreamName, object? Value);

    private sealed class OutputCapture(PowerShellWasmExecutionContext context, List<object?> output) : IDisposable
    {
        public void Dispose() =>
            context.ReleaseOutputCapture(output);
    }

    private sealed class PipelineItemScope(
        PowerShellWasmExecutionContext context,
        bool hadUnderscore,
        object? underscore,
        bool hadPSItem,
        object? psItem) : IDisposable
    {
        public void Dispose() =>
            context.RestorePipelineItem(hadUnderscore, underscore, hadPSItem, psItem);
    }
}
