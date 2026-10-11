using System.Globalization;
using System.Text.RegularExpressions;

namespace PSWasm.Commands;

internal sealed class SelectStringCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        context.Parameters.ContainsKey("InputObject")
            ? new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput)
            : new PowerShellWasmPerRecordProcessor(this, context, hasPipelineInput);

    public IReadOnlyCollection<string> SwitchParameters => ["CaseSensitive", "NotMatch", "AllMatches"];

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var patterns = GetPatterns(context);
        if (patterns.Count == 0)
        {
            return;
        }

        var options = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("CaseSensitive"))
            ? RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        var notMatch = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("NotMatch"));
        var allMatches = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("AllMatches"));

        foreach (var line in GetInput(context))
        {
            var lineText = Convert.ToString(line, CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(lineText, pattern, options).Cast<Match>().Where(static match => match.Success).ToArray();
                if (notMatch)
                {
                    if (matches.Length == 0)
                    {
                        await context.ExecutionContext.WriteOutputAsync(CreateResult(lineText, pattern, []), cancellationToken);
                    }

                    continue;
                }

                if (matches.Length == 0)
                {
                    continue;
                }

                var selectedMatches = allMatches ? matches : matches.Take(1).ToArray();
                await context.ExecutionContext.WriteOutputAsync(CreateResult(lineText, pattern, selectedMatches), cancellationToken);
            }
        }

        return;
    }

    private static IReadOnlyList<string> GetPatterns(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Pattern", out var pattern))
        {
            return Enumerate(pattern).Select(ToInvariantString).Where(static item => item.Length > 0).ToArray();
        }

        return context.Arguments.Take(1).Select(ToInvariantString).Where(static item => item.Length > 0).ToArray();
    }

    private static IEnumerable<object?> GetInput(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            return Enumerate(inputObject);
        }

        return PowerShellWasmCommandUtilities.EnumeratePipelineValues(context.PipelineInput);
    }

    private static Dictionary<string, object?> CreateResult(string line, string pattern, IReadOnlyList<Match> matches) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Line"] = line,
            ["Pattern"] = pattern,
            ["Matches"] = matches.Select(CreateMatch).Cast<object?>().ToArray()
        };

    private static Dictionary<string, object?> CreateMatch(Match match) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Value"] = match.Value,
            ["Index"] = match.Index,
            ["Length"] = match.Length
        };

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

    private static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
