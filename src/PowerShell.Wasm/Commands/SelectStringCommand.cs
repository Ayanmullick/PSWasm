using System.Globalization;
using System.Text.RegularExpressions;

namespace PSWasm.Commands;

internal sealed class SelectStringCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var patterns = GetPatterns(context);
        if (patterns.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var options = context.Parameters.ContainsKey("CaseSensitive")
            ? RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        var notMatch = context.Parameters.ContainsKey("NotMatch");
        var allMatches = context.Parameters.ContainsKey("AllMatches");

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
                        context.ExecutionContext.WriteOutput(CreateResult(lineText, pattern, []));
                    }

                    continue;
                }

                if (matches.Length == 0)
                {
                    continue;
                }

                var selectedMatches = allMatches ? matches : matches.Take(1).ToArray();
                context.ExecutionContext.WriteOutput(CreateResult(lineText, pattern, selectedMatches));
            }
        }

        return ValueTask.CompletedTask;
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

        return PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput);
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
