using System.Globalization;

namespace PSWasm.Commands;

internal static class PowerShellWasmCommandUtilities
{
    public static IEnumerable<object?> EnumerateInput(IReadOnlyList<object?> input)
    {
        foreach (var item in EnumeratePipelineInput(input))
        {
            yield return item.Value;
        }
    }

    public static IEnumerable<PowerShellWasmPipelineInputItem> EnumeratePipelineInput(IReadOnlyList<object?> input)
    {
        foreach (var item in input)
        {
            var itemVariables = PowerShellWasmPipelineValue.GetVariables(item);
            foreach (var value in Enumerate(PowerShellWasmPipelineValue.Unwrap(item)))
            {
                var variables = MergePipelineVariables(itemVariables, PowerShellWasmPipelineValue.GetVariables(value));
                yield return new(
                    PowerShellWasmPipelineValue.Unwrap(value),
                    variables);
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> MergePipelineVariables(
        IReadOnlyDictionary<string, object?> first,
        IReadOnlyDictionary<string, object?> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, object?>(first, StringComparer.OrdinalIgnoreCase);
        foreach (var variable in second)
        {
            merged[variable.Key] = variable.Value;
        }

        return merged;
    }

    public static PowerShellWasmScriptBlock? GetScriptBlock(PowerShellWasmCommandContext context, params string[] parameterNames)
    {
        foreach (var name in parameterNames)
        {
            if (context.Parameters.TryGetValue(name, out var parameterValue) &&
                parameterValue is PowerShellWasmScriptBlock parameterScriptBlock)
            {
                return parameterScriptBlock;
            }
        }

        return context.Arguments.OfType<PowerShellWasmScriptBlock>().FirstOrDefault();
    }

    public static bool ToBoolean(object? value) =>
        value switch
        {
            null => false,
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            double doubleValue => Math.Abs(doubleValue) > 0.0000000001,
            decimal decimalValue => decimalValue != 0,
            string stringValue => stringValue.Length > 0,
            object?[] arrayValue => arrayValue.Any(ToBoolean),
            _ => true
        };

    public static object? GetMemberValue(object? target, string memberName)
    {
        return TryGetMemberValue(target, memberName, out var value) ? value : null;
    }

    public static bool TryGetMemberValue(object? target, string memberName, out object? value)
    {
        value = null;
        if (target is null)
        {
            return false;
        }

        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            readOnlyDictionary.TryGetValue(memberName, out var readOnlyValue))
        {
            value = readOnlyValue;
            return true;
        }

        if (target is IDictionary<string, object?> dictionary && dictionary.TryGetValue(memberName, out value))
        {
            return true;
        }

        if (target is System.Collections.IDictionary legacyDictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
            {
                if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), memberName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        if (TryGetCollectionProperty(target, memberName, out value))
        {
            return true;
        }

        return false;
    }

    public static int CompareValues(object? left, object? right)
    {
        if (TryToNumber(left, out var leftNumber) && TryToNumber(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.Compare(ToInvariantString(left), ToInvariantString(right), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryToNumber(object? value, out double number)
    {
        switch (value)
        {
            case bool boolValue:
                number = boolValue ? 1 : 0;
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    public static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    public static string FormatValue(object? value) =>
        value switch
        {
            null => string.Empty,
            IReadOnlyDictionary<string, object?> readOnlyDictionary => FormatDictionary(readOnlyDictionary),
            IDictionary<string, object?> dictionary => FormatDictionary(dictionary),
            System.Collections.IDictionary legacyDictionary => FormatLegacyDictionary(legacyDictionary),
            object?[] array => string.Join(Environment.NewLine, array.Select(FormatValue)),
            _ => ToInvariantString(value)
        };

    public static IReadOnlyList<string> GetPropertyNames(PowerShellWasmCommandContext context)
    {
        var names = new List<string>();
        if (context.Parameters.TryGetValue("Property", out var property))
        {
            AddNames(property);
        }

        foreach (var argument in context.Arguments)
        {
            if (argument is PowerShellWasmScriptBlock)
            {
                continue;
            }

            AddNames(argument);
        }

        return names;

        void AddNames(object? value)
        {
            foreach (var item in Enumerate(value))
            {
                var text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    names.Add(text);
                }
            }
        }
    }

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IDictionary or IReadOnlyDictionary<string, object?>)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private static bool TryGetCollectionProperty(object? target, string memberName, out object? value)
    {
        value = null;
        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            if (IsCountLike(memberName))
            {
                value = readOnlyDictionary.Count;
                return true;
            }

            if (memberName.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                value = readOnlyDictionary.Keys.Cast<object?>().ToArray();
                return true;
            }

            if (memberName.Equals("Values", StringComparison.OrdinalIgnoreCase))
            {
                value = readOnlyDictionary.Values.ToArray();
                return true;
            }
        }

        if (target is System.Collections.IDictionary dictionary)
        {
            if (IsCountLike(memberName))
            {
                value = dictionary.Count;
                return true;
            }

            if (memberName.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Keys.Cast<object?>().ToArray();
                return true;
            }

            if (memberName.Equals("Values", StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary.Values.Cast<object?>().ToArray();
                return true;
            }
        }

        if (memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) && target is string text)
        {
            value = text.Length;
            return true;
        }

        if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) && target is string)
        {
            value = 1;
            return true;
        }

        if (IsCountLike(memberName))
        {
            value = Enumerate(target).Count();
            return true;
        }

        if (memberName.Equals("Rank", StringComparison.OrdinalIgnoreCase))
        {
            value = target is System.Collections.IEnumerable and not string ? 1 : 0;
            return true;
        }

        return false;
    }

    private static bool IsCountLike(string memberName) =>
        memberName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
        memberName.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
        memberName.Equals("LongLength", StringComparison.OrdinalIgnoreCase);

    private static string FormatDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary) =>
        "@{" + string.Join("; ", dictionary.Select(static item => $"{item.Key}={FormatValue(item.Value)}")) + "}";

    private static string FormatLegacyDictionary(System.Collections.IDictionary dictionary)
    {
        var items = new List<string>();
        foreach (System.Collections.DictionaryEntry item in dictionary)
        {
            items.Add($"{ToInvariantString(item.Key)}={FormatValue(item.Value)}");
        }

        return "@{" + string.Join("; ", items) + "}";
    }
}
