using System.Globalization;
using System.Net;

namespace PSWasm.Commands;

internal sealed class ConvertToHtmlCommand : IPowerShellWasmCommand
{
    private const string DefaultTitle = "HTML TABLE";

    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = GetInput(context).ToArray();
        var lines = CreateHtml(input, context, cancellationToken);
        foreach (var line in lines)
        {
            context.ExecutionContext.WriteOutput(line);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<object?> GetInput(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            return PowerShellWasmCommandUtilities.EnumerateInput([inputObject]);
        }

        if (context.PipelineInput.Count > 0)
        {
            return PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput);
        }

        return context.Arguments.Count > 0 ? PowerShellWasmCommandUtilities.EnumerateInput(context.Arguments) : [];
    }

    private static IReadOnlyList<string> CreateHtml(
        IReadOnlyList<object?> input,
        PowerShellWasmCommandContext context,
        CancellationToken cancellationToken)
    {
        var fragment = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Fragment"));
        var lines = new List<string>();
        if (!fragment)
        {
            lines.Add("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"  \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">");
            lines.Add("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
            lines.Add("<head>");
            AddHead(lines, context);
            lines.Add("</head><body>");
            lines.AddRange(GetStringValues(context, "Body"));
        }

        lines.AddRange(GetStringValues(context, "PreContent"));
        lines.AddRange(CreateTable(input, context, cancellationToken));
        lines.AddRange(GetStringValues(context, "PostContent"));

        if (!fragment)
        {
            lines.Add("</body></html>");
        }

        return lines;
    }

    private static void AddHead(List<string> lines, PowerShellWasmCommandContext context)
    {
        var head = GetStringValues(context, "Head").ToArray();
        if (head.Length > 0)
        {
            lines.AddRange(head);
            return;
        }

        lines.Add($"<title>{context.GetString("Title") ?? DefaultTitle}</title>");
    }

    private static IReadOnlyList<string> CreateTable(
        IReadOnlyList<object?> input,
        PowerShellWasmCommandContext context,
        CancellationToken cancellationToken)
    {
        var properties = GetPropertyNames(context);
        if (properties.Count == 0)
        {
            properties = FormatCommandUtilities.GetPropertyNames(input);
        }

        var lines = new List<string>
        {
            "<table>",
            "<colgroup>" + string.Concat(properties.Select(static _ => "<col/>")) + "</colgroup>",
            "<tr>" + string.Concat(properties.Select(static property => $"<th>{HtmlEncode(property)}</th>")) + "</tr>"
        };

        foreach (var item in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add("<tr>" + string.Concat(properties.Select(property =>
                $"<td>{HtmlEncode(FormatCommandUtilities.FormatCell(FormatCommandUtilities.GetPropertyValue(item, property)))}</td>")) + "</tr>");
        }

        lines.Add("</table>");
        return lines;
    }

    private static IReadOnlyList<string> GetPropertyNames(PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Property", out var property))
        {
            return [];
        }

        return PowerShellWasmCommandUtilities.GetPropertyNames(context);
    }

    private static IEnumerable<string> GetStringValues(PowerShellWasmCommandContext context, string parameterName)
    {
        if (!context.Parameters.TryGetValue(parameterName, out var value))
        {
            yield break;
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
        {
            yield return Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static string HtmlEncode(string value) =>
        WebUtility.HtmlEncode(value);
}
