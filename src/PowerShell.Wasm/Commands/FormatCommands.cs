using System.Globalization;

namespace PSWasm.Commands;

internal sealed class FormatListCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = FormatCommandUtilities.GetInput(context).ToArray();
        if (input.Length == 0)
        {
            return;
        }

        var explicitProperties = PowerShellWasmCommandUtilities.GetPropertyNames(context);
        var blocks = new List<string>();
        foreach (var item in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = explicitProperties.Count > 0 ? explicitProperties : FormatCommandUtilities.GetPropertyNames(item);
            var width = properties.Count == 0 ? 0 : properties.Max(static property => property.Length);
            blocks.Add(string.Join(Environment.NewLine, properties.Select(property =>
                $"{property.PadRight(width)} : {FormatCommandUtilities.FormatCell(FormatCommandUtilities.GetPropertyValue(item, property))}")));
        }

        await context.ExecutionContext.WriteOutputAsync(string.Join(Environment.NewLine + Environment.NewLine, blocks), cancellationToken);
        return;
    }
}

internal sealed class FormatTableCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);

    public IReadOnlyCollection<string> SwitchParameters => ["HideTableHeaders"];

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = FormatCommandUtilities.GetInput(context).ToArray();
        if (input.Length == 0)
        {
            return;
        }

        var properties = PowerShellWasmCommandUtilities.GetPropertyNames(context);
        if (properties.Count == 0)
        {
            properties = FormatCommandUtilities.GetPropertyNames(input);
        }

        var rows = input.Select(item =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return properties.Select(property => FormatCommandUtilities.FormatCell(
                FormatCommandUtilities.GetPropertyValue(item, property))).ToArray();
        }).ToArray();

        var widths = properties.Select((property, index) =>
            Math.Max(property.Length, rows.Max(row => row[index].Length))).ToArray();
        var hideHeaders = context.Parameters.TryGetValue("HideTableHeaders", out var hideValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(hideValue);

        var lines = new List<string>();
        if (!hideHeaders)
        {
            lines.Add(JoinCells(properties, widths));
            lines.Add(JoinCells(widths.Select(static width => new string('-', width)).ToArray(), widths));
        }

        lines.AddRange(rows.Select(row => JoinCells(row, widths)));
        await context.ExecutionContext.WriteOutputAsync(string.Join(Environment.NewLine, lines), cancellationToken);
        return;
    }

    private static string JoinCells(IReadOnlyList<string> cells, IReadOnlyList<int> widths) =>
        string.Join("  ", cells.Select((cell, index) => cell.PadRight(widths[index]))).TrimEnd();
}

internal static class FormatCommandUtilities
{
    public static IEnumerable<object?> GetInput(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            return PowerShellWasmCommandUtilities.EnumerateInput([inputObject]);
        }

        return context.PipelineInput.Count > 0
            ? PowerShellWasmCommandUtilities.EnumeratePipelineValues(context.PipelineInput).Where(static item => item is not null)
            : [];
    }

    public static IReadOnlyList<string> GetPropertyNames(IReadOnlyList<object?> input)
    {
        var names = new List<string>();
        foreach (var item in input)
        {
            foreach (var name in GetPropertyNames(item))
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        return names.Count > 0 ? names : ["Value"];
    }

    public static IReadOnlyList<string> GetPropertyNames(object? item)
    {
        if (item is System.Collections.DictionaryEntry) return ["Name", "Value"];

        if (item is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return readOnlyDictionary.Keys.ToArray();
        }

        if (item is IDictionary<string, object?> dictionary)
        {
            return dictionary.Keys.ToArray();
        }

        if (item is System.Collections.IDictionary legacyDictionary)
        {
            var names = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in legacyDictionary)
            {
                var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        return ["Value"];
    }

    public static object? GetPropertyValue(object? item, string propertyName)
    {
        var value = PowerShellWasmCommandUtilities.GetMemberValue(item, propertyName);
        return value is null && propertyName.Equals("Value", StringComparison.OrdinalIgnoreCase) &&
            GetPropertyNames(item).SequenceEqual(["Value"], StringComparer.OrdinalIgnoreCase)
                ? item
                : value;
    }

    public static string FormatCell(object? value) =>
        PowerShellWasmCommandUtilities.FormatValue(value)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
