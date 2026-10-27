namespace PSWasm.Commands;

internal sealed class GetVariableCommand : IPowerShellWasmPipelineCommand
{
    public IReadOnlyCollection<string> SwitchParameters => ["ValueOnly"];

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var valueOnly = context.Parameters.TryGetValue("ValueOnly", out var valueOnlyValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(valueOnlyValue);
        var variables = SelectVariables(context);

        foreach (var variable in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await context.ExecutionContext.WriteOutputAsync(valueOnly ? variable.Value : CreateVariableRecord(variable.Key, variable.Value), cancellationToken);
        }

        return;
    }

    private static IEnumerable<KeyValuePair<string, object?>> SelectVariables(PowerShellWasmCommandContext context)
    {
        var variables = context.ExecutionContext.GetRawVariables(VariableCommandUtilities.GetScope(context));
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            return variables.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase);
        }

        return VariableCommandUtilities.ResolveNames(context, names, variables)
            .Select(name => new KeyValuePair<string, object?>(name, variables[name]));
    }

    private static Dictionary<string, object?> CreateVariableRecord(string name, object? value) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["Value"] = value
        };
}

internal sealed class SetVariableCommand : IPowerShellWasmPipelineCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Set-Variable requires a variable name.");
        }

        var scope = VariableCommandUtilities.GetScope(context);
        var variables = context.ExecutionContext.GetRawVariables(scope, searchParents: false);
        var value = GetValue(context);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = variables.Keys.Where(variableName => VariableCommandUtilities.NameMatches(variableName, name)).ToArray();
            if (matches.Length == 0)
            {
                if (!VariableCommandUtilities.HasWildcard(name)) context.ExecutionContext.SetNewVariable(name, value, scope);
                continue;
            }

            // Matched variable names are literal; only a new unscoped name can contain a scope qualifier.
            foreach (var match in matches) context.ExecutionContext.SetVariable(match, value, scope ?? "local");
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

internal sealed class ClearVariableCommand : IPowerShellWasmPipelineCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Clear-Variable requires a variable name.");
        }

        var scope = VariableCommandUtilities.GetScope(context);
        var variables = context.ExecutionContext.GetRawVariables(scope);
        foreach (var name in VariableCommandUtilities.ResolveNames(context, names, variables))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.ClearVariable(name, scope);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RemoveVariableCommand : IPowerShellWasmPipelineCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var names = VariableCommandUtilities.GetNames(context).ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Remove-Variable requires a variable name.");
        }

        var scope = VariableCommandUtilities.GetScope(context);
        var variables = context.ExecutionContext.GetRawVariables(scope, searchParents: false);
        foreach (var name in VariableCommandUtilities.ResolveNames(context, names, variables))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.RemoveVariable(name, scope ?? "local");
        }

        return ValueTask.CompletedTask;
    }
}

internal static class VariableCommandUtilities
{
    // Behavioral guidance: variable cmdlets select an exact -Scope view; only unscoped Get/Clear search parents.
    // Removal defaults to local. Names are matched literally within that view before wildcard expansion.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Var.cs#L148-L160
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Var.cs#L1140-L1169
    public static string? GetScope(PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.ContainsKey("Scope")) return null;
        var scope = context.GetString("Scope");
        return string.IsNullOrEmpty(scope)
            ? throw new InvalidOperationException("The Scope parameter cannot be null or empty.") : scope;
    }

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

    public static IEnumerable<string> ResolveNames(PowerShellWasmCommandContext context, IEnumerable<string> names,
        IReadOnlyDictionary<string, object?> variables)
    {
        foreach (var name in names)
        {
            var matches = variables.Keys.Where(variableName => NameMatches(variableName, name))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length == 0 && !HasWildcard(name))
            {
                context.ExecutionContext.WriteStream("Error", $"Cannot find a variable with the name '{name}'.");
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

    public static bool HasWildcard(string value) =>
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
