namespace PSWasm.Commands;

internal sealed class GetVariableCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var valueOnly = context.Parameters.TryGetValue("ValueOnly", out var valueOnlyValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(valueOnlyValue);
        var variables = SelectVariables(context).OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(valueOnly ? variable.Value : CreateVariableRecord(variable.Key, variable.Value));
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<KeyValuePair<string, object?>> SelectVariables(PowerShellWasmCommandContext context)
    {
        var variables = context.ExecutionContext.GetVariables();
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            return variables;
        }

        return names.SelectMany(name => variables.Where(item => VariableCommandUtilities.NameMatches(item.Key, name)));
    }

    private static Dictionary<string, object?> CreateVariableRecord(string name, object? value) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["Value"] = value
        };
}

internal sealed class SetVariableCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Set-Variable requires a variable name.");
        }

        var value = GetValue(context);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.SetVariable(name, value);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> GetNames(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var parameterName))
        {
            return VariableCommandUtilities.EnumerateNames(parameterName);
        }

        return context.Arguments.Count > 0 ? VariableCommandUtilities.EnumerateNames(context.Arguments[0]) : [];
    }

    private static object? GetValue(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Value", out var value))
        {
            return value;
        }

        var nameArgumentCount = context.Parameters.ContainsKey("Name") ? 0 : 1;
        return context.Arguments.Count > nameArgumentCount ? context.Arguments[nameArgumentCount] : null;
    }
}

internal sealed class ClearVariableCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Clear-Variable requires a variable name.");
        }

        foreach (var name in VariableCommandUtilities.ResolveNames(context, names))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.ClearVariable(name);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RemoveVariableCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Remove-Variable requires a variable name.");
        }

        foreach (var name in VariableCommandUtilities.ResolveNames(context, names))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.RemoveVariable(name);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class VariableCommandUtilities
{
    public static IEnumerable<string> GetNames(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var parameterName))
        {
            foreach (var name in EnumerateNames(parameterName))
            {
                yield return name;
            }
        }

        foreach (var argument in context.Arguments)
        {
            foreach (var name in EnumerateNames(argument))
            {
                yield return name;
            }
        }
    }

    public static IEnumerable<string> ResolveNames(PowerShellWasmCommandContext context, IEnumerable<string> names)
    {
        var variables = context.ExecutionContext.GetVariables();
        foreach (var name in names)
        {
            var matches = variables.Keys.Where(variableName => NameMatches(variableName, name)).ToArray();
            if (matches.Length == 0 && !HasWildcard(name))
            {
                yield return name;
                continue;
            }

            foreach (var match in matches)
            {
                yield return match;
            }
        }
    }

    public static bool NameMatches(string name, string pattern)
    {
        if (!HasWildcard(pattern))
        {
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        return MatchWildcard(name, pattern);
    }

    public static IEnumerable<string> EnumerateNames(object? value)
    {
        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
        {
            var name = PowerShellWasmCommandUtilities.ToInvariantString(item);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static bool HasWildcard(string value) =>
        value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

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
