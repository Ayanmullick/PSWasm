namespace PSWasm.Commands;

internal sealed class SelectObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = PowerShellWasmCommandUtilities.EnumeratePipelineInput(context.PipelineInput).ToArray();
        var selected = ApplyWindow(input, context);
        var expandProperty = context.GetString("ExpandProperty");
        var properties = GetProperties(context);

        foreach (var item in selected)
        {
            if (!string.IsNullOrWhiteSpace(expandProperty))
            {
                var value = PowerShellWasmCommandUtilities.GetMemberValue(item.Value, expandProperty);
                context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(value, item.Variables));
                continue;
            }

            if (properties.Count > 0)
            {
                context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(
                    await SelectPropertiesAsync(item, properties, cancellationToken),
                    item.Variables));
                continue;
            }

            context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(item.Value, item.Variables));
        }
    }

    private static IEnumerable<PowerShellWasmPipelineInputItem> ApplyWindow(
        IReadOnlyList<PowerShellWasmPipelineInputItem> input,
        PowerShellWasmCommandContext context)
    {
        var skip = Math.Max(0, context.GetInt32("Skip", 0));
        var first = context.Parameters.ContainsKey("First") ? Math.Max(0, context.GetInt32("First", input.Count)) : (int?)null;
        var last = context.Parameters.ContainsKey("Last") ? Math.Max(0, context.GetInt32("Last", input.Count)) : (int?)null;

        var selected = input.Skip(skip);
        if (last is not null)
        {
            selected = selected.TakeLast(last.Value);
        }

        if (first is not null)
        {
            selected = selected.Take(first.Value);
        }

        return selected;
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
