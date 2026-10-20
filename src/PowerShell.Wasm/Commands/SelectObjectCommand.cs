namespace PSWasm.Commands;

internal sealed class SelectObjectCommand : IPowerShellWasmPipelineCommand
{
    public IReadOnlyCollection<string> SwitchParameters => ["Wait"];

    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new Processor(context);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var processor = new Processor(context);
        await processor.BeginAsync(cancellationToken);
        foreach (var record in context.PipelineInput)
        {
            await processor.ProcessAsync(record, cancellationToken);
            if (processor.IsInputComplete) break;
        }
        await processor.CompleteAsync(cancellationToken);
    }

    // Behavioral guidance: SelectObjectQueue streams the head and retains a disjoint tail; positive First can stop input.
    // Browser adaptation delegates upstream interruption to the runtime pipeline, preserving awaited output delivery.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Select-Object.cs#L233-L336
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Select-Object.cs#L723-L751
    private sealed class Processor : IPowerShellWasmPipelineProcessor
    {
        private readonly PowerShellWasmCommandContext _context;
        private readonly string? _expandProperty;
        private readonly IReadOnlyList<SelectProperty> _properties;
        private readonly int _skip, _first, _last;
        private readonly bool _selectionSpecified, _wait;
        private readonly Queue<PowerShellWasmPipelineInputItem> _tail = new();
        private int _skipRemaining, _emitted;

        public bool IsInputComplete { get; private set; }

        public Processor(PowerShellWasmCommandContext context)
        {
            _context = context;
            _expandProperty = context.GetString("ExpandProperty");
            _properties = GetProperties(context);
            _skip = Math.Max(0, context.GetInt32("Skip", 0));
            _first = context.Parameters.ContainsKey("First") ? Math.Max(0, context.GetInt32("First", int.MaxValue)) : 0;
            _last = context.Parameters.ContainsKey("Last") ? Math.Max(0, context.GetInt32("Last", int.MaxValue)) : 0;
            _selectionSpecified = context.Parameters.ContainsKey("First") || context.Parameters.ContainsKey("Last");
            _wait = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Wait"));
            _skipRemaining = _last == 0 || _first > 0 ? _skip : 0;
        }

        public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = PowerShellWasmPipelineValue.Unwrap(record);
            if (value is null || PowerShellWasmOutput.IsNoOutput(value)) return;
            if (_skipRemaining > 0)
            {
                _skipRemaining--;
                return;
            }

            var item = new PowerShellWasmPipelineInputItem(value, PowerShellWasmPipelineValue.GetVariables(record));
            if (!_selectionSpecified || _emitted < _first)
            {
                _emitted++;
                await WriteItemAsync(item, cancellationToken);
                // The input record counts even if ExpandProperty produces no output or expands to many records.
                IsInputComplete = !_wait && _first > 0 && _last == 0 && _emitted >= _first;
            }
            else if (_last > 0)
            {
                _tail.Enqueue(item);
                var capacity = (long)_last + (_first == 0 ? _skip : 0);
                if (_tail.Count > capacity) _tail.Dequeue();
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            var skipTail = _first == 0 ? _skip : 0;
            while (_tail.Count > skipTail)
            {
                await WriteItemAsync(_tail.Dequeue(), cancellationToken);
            }
        }

        private async ValueTask WriteItemAsync(PowerShellWasmPipelineInputItem item, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_expandProperty))
            {
                // Behavioral guidance: ExpandProperty enumerates once and omits null elements.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Select-Object.cs#L537-L566
                var value = PowerShellWasmCommandUtilities.GetMemberValue(item.Value, _expandProperty);
                if (value is null) return;
                foreach (var output in PowerShellWasmOutput.Enumerate(value))
                    if (output is not null)
                        await _context.ExecutionContext.WriteOutputAsync(
                            PowerShellWasmPipelineValue.Wrap(output, item.Variables), cancellationToken);
                return;
            }

            var selected = _properties.Count > 0
                ? await SelectPropertiesAsync(item, _properties, cancellationToken) : item.Value;
            await _context.ExecutionContext.WriteOutputAsync(
                PowerShellWasmPipelineValue.Wrap(selected, item.Variables), cancellationToken);
        }
    }

    private static IReadOnlyList<SelectProperty> GetProperties(PowerShellWasmCommandContext context)
    {
        var properties = new List<SelectProperty>();
        if (context.Parameters.TryGetValue("Property", out var property))
        {
            AddProperties(properties, property);
        }

        foreach (var argument in context.Arguments)
        {
            if (argument is PowerShellWasmScriptBlock)
            {
                continue;
            }

            AddProperties(properties, argument);
        }

        return properties;
    }

    private static void AddProperties(List<SelectProperty> properties, object? value)
    {
        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
        {
            var property = CreateProperty(item, properties.Count);
            if (!string.IsNullOrWhiteSpace(property.Name))
            {
                properties.Add(property);
            }
        }
    }

    private static SelectProperty CreateProperty(object? value, int index)
    {
        if (TryAsDictionary(value, out var dictionary))
        {
            var name = GetAliasValue(dictionary, "Name", "Label", "N");
            var expression = GetAliasValue(dictionary, "Expression", "E");
            var expressionName = expression is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
            return new(
                PowerShellWasmCommandUtilities.ToInvariantString(name ?? expressionName ?? $"Expression{index + 1}"),
                expression ?? name,
                IsCalculated: true);
        }

        var propertyName = PowerShellWasmCommandUtilities.ToInvariantString(value);
        return new(propertyName, propertyName, IsCalculated: false);
    }

    private static async ValueTask<Dictionary<string, object?>> SelectPropertiesAsync(
        PowerShellWasmPipelineInputItem item,
        IReadOnlyList<SelectProperty> properties,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            foreach (var expandedProperty in ExpandProperty(item.Value, property))
            {
                result[expandedProperty.Name] =
                    await EvaluatePropertyAsync(item, expandedProperty.Expression, cancellationToken);
            }
        }

        return result;
    }

    private static IEnumerable<SelectProperty> ExpandProperty(object? item, SelectProperty property)
    {
        if (property.IsCalculated || property.Expression is not string expression || !HasWildcard(expression))
        {
            yield return property;
            yield break;
        }

        foreach (var propertyName in FormatCommandUtilities.GetPropertyNames(item)
            .Where(name => VariableCommandUtilities.NameMatches(name, expression)))
        {
            yield return new(propertyName, propertyName, IsCalculated: false);
        }
    }

    private static async ValueTask<object?> EvaluatePropertyAsync(
        PowerShellWasmPipelineInputItem item,
        object? expression,
        CancellationToken cancellationToken)
    {
        if (expression is PowerShellWasmScriptBlock scriptBlock)
        {
            var output = await scriptBlock.InvokeAsync(item.Value, null, item.Variables, cancellationToken);
            return output.Count switch
            {
                0 => null,
                1 => output[0],
                _ => output.ToArray()
            };
        }

        var propertyName = PowerShellWasmCommandUtilities.ToInvariantString(expression);
        return string.IsNullOrWhiteSpace(propertyName)
            ? null
            : PowerShellWasmCommandUtilities.GetMemberValue(item.Value, propertyName);
    }

    private static object? GetAliasValue(IReadOnlyDictionary<string, object?> dictionary, params string[] names)
    {
        foreach (var name in names)
        {
            if (dictionary.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryAsDictionary(object? value, out IReadOnlyDictionary<string, object?> dictionary)
    {
        dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                dictionary = new Dictionary<string, object?>(readOnlyDictionary, StringComparer.OrdinalIgnoreCase);
                return true;
            case IDictionary<string, object?> genericDictionary:
                dictionary = new Dictionary<string, object?>(genericDictionary, StringComparer.OrdinalIgnoreCase);
                return true;
            case System.Collections.IDictionary legacyDictionary:
                var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
                {
                    var key = PowerShellWasmCommandUtilities.ToInvariantString(entry.Key);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        converted[key] = entry.Value;
                    }
                }

                dictionary = converted;
                return true;
            default:
                return false;
        }
    }

    private static bool HasWildcard(string value) =>
        value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

    private sealed record SelectProperty(string Name, object? Expression, bool IsCalculated);
}
