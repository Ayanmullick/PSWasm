namespace PSWasm.Commands;

internal sealed class GetCommandCommand(Func<IEnumerable<string>> getCommandNames) : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var patterns = GetPatterns(context).ToArray();
        var names = getCommandNames()
            .Where(name => patterns.Length == 0 || patterns.Any(pattern => NameMatches(name, pattern)))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(CreateCommandRecord(name));
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> GetPatterns(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var name))
        {
            foreach (var pattern in EnumeratePatterns(name))
            {
                yield return pattern;
            }
        }

        foreach (var argument in context.Arguments)
        {
            foreach (var pattern in EnumeratePatterns(argument))
            {
                yield return pattern;
            }
        }
    }

    private static IEnumerable<string> EnumeratePatterns(object? value)
    {
        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
        {
            var pattern = PowerShellWasmCommandUtilities.ToInvariantString(item);
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                yield return pattern;
            }
        }
    }

    private static Dictionary<string, object?> CreateCommandRecord(string name) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["CommandType"] = "BrowserCommand"
        };

    private static bool NameMatches(string name, string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        return MatchWildcard(name, pattern);
    }

    private static bool MatchWildcard(string name, string pattern)
    {
        var nameIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (nameIndex < name.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(name[nameIndex])))
            {
                nameIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = nameIndex;
                continue;
            }

            if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                nameIndex = ++matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
