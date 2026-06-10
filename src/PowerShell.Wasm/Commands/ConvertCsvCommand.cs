using System.Globalization;
using System.Text;

namespace PSWasm.Commands;

internal sealed class ConvertFromCsvCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var delimiter = GetDelimiter(context);
        var rows = ParseRows(GetInputText(context), delimiter)
            .Where(static row => !IsBlankRow(row))
            .ToArray();

        if (rows.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        var header = GetHeader(context);
        var dataStart = 0;
        if (header.Count == 0)
        {
            header = CreateHeader(rows[0]);
            dataStart = 1;
        }

        for (var i = dataStart; i < rows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(CreateRow(header, rows[i]));
        }

        return ValueTask.CompletedTask;
    }

    private static char GetDelimiter(PowerShellWasmCommandContext context)
    {
        var value = context.GetString("Delimiter");
        return string.IsNullOrEmpty(value) ? ',' : value[0];
    }

    private static string GetInputText(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            return JoinInput(PowerShellWasmCommandUtilities.EnumerateInput([inputObject]));
        }

        if (context.PipelineInput.Count > 0)
        {
            return JoinInput(context.PipelineInput);
        }

        return JoinInput(PowerShellWasmCommandUtilities.EnumerateInput(context.Arguments));
    }

    private static string JoinInput(IEnumerable<object?> input) =>
        string.Join(Environment.NewLine, input.Select(PowerShellWasmCommandUtilities.ToInvariantString));

    private static IReadOnlyList<string> GetHeader(PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Header", out var header))
        {
            return [];
        }

        return PowerShellWasmCommandUtilities.EnumerateInput([header])
            .Select(PowerShellWasmCommandUtilities.ToInvariantString)
            .Where(static value => value.Length > 0)
            .Select(static value => value.Trim())
            .ToArray();
    }

    private static IReadOnlyList<string> CreateHeader(IReadOnlyList<string> row) =>
        row.Select(static value => value.Trim()).ToArray();

    private static Dictionary<string, object?> CreateRow(IReadOnlyList<string> header, IReadOnlyList<string> row)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"H{i + 1}";
            }

            result[name] = i < row.Count ? row[i] : string.Empty;
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseRows(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var sawValue = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                field.Append(ch);
                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
                sawValue = true;
                continue;
            }

            if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                sawValue = true;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                AddRow();
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            field.Append(ch);
            sawValue = true;
        }

        AddRow();
        return rows;

        void AddRow()
        {
            if (!sawValue && field.Length == 0 && row.Count == 0)
            {
                return;
            }

            row.Add(field.ToString());
            rows.Add(row.ToArray());
            row.Clear();
            field.Clear();
            sawValue = false;
        }
    }

    private static bool IsBlankRow(IReadOnlyList<string> row) =>
        row.Count == 0 || row.All(static value => string.IsNullOrWhiteSpace(value));
}
