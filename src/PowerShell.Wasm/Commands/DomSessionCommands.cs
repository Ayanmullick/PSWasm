namespace PSWasm.Commands;

internal sealed class NewDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ExecutionContext.WriteOutput(context.ExecutionContext.NewDomSession(GetName(context), GetTarget(context)));
        return ValueTask.CompletedTask;
    }

    private static string? GetName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var name))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(name);
        }

        return context.Arguments.Count > 0 ? PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[0]) : null;
    }

    private static string? GetTarget(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Target", out var target))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(target);
        }

        return context.Arguments.Count > 1 ? PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[1]) : null;
    }
}

internal sealed class GetDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var sessions = SelectSessions(context);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(session);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<Dictionary<string, object?>> SelectSessions(PowerShellWasmCommandContext context)
    {
        var sessions = context.ExecutionContext.GetDomSessions();
        var ids = DomSessionCommandUtilities.GetIds(context).ToArray();
        if (ids.Length > 0)
        {
            return sessions.Where(session => ids.Contains(Convert.ToInt32(session["Id"], System.Globalization.CultureInfo.InvariantCulture)));
        }

        var names = DomSessionCommandUtilities.GetNames(context).ToArray();
        return names.Length == 0 ? sessions : sessions.Where(session =>
            names.Any(name => VariableCommandUtilities.NameMatches(PowerShellWasmCommandUtilities.ToInvariantString(session["Name"]), name)));
    }
}

internal sealed class RemoveDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var sessions = GetDomSessionCommandSessions(context).ToArray();
        if (sessions.Length == 0)
        {
            throw new InvalidOperationException("Remove-DomSession requires an existing DOM session name or id.");
        }

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.RemoveDomSession(Convert.ToInt32(session["Id"], System.Globalization.CultureInfo.InvariantCulture));
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<Dictionary<string, object?>> GetDomSessionCommandSessions(PowerShellWasmCommandContext context)
    {
        var ids = DomSessionCommandUtilities.GetIds(context).ToArray();
        if (ids.Length > 0)
        {
            return context.ExecutionContext.GetDomSessions().Where(session =>
                ids.Contains(Convert.ToInt32(session["Id"], System.Globalization.CultureInfo.InvariantCulture)));
        }

        var names = DomSessionCommandUtilities.GetNames(context).ToArray();
        return names.Length == 0 ? [] : context.ExecutionContext.GetDomSessions().Where(session =>
            names.Any(name => VariableCommandUtilities.NameMatches(PowerShellWasmCommandUtilities.ToInvariantString(session["Name"]), name)));
    }
}

internal static class DomSessionCommandUtilities
{
    public static IEnumerable<int> GetIds(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Id", out var id))
        {
            foreach (var item in EnumerateIds(id))
            {
                yield return item;
            }
        }

        foreach (var argument in context.Arguments)
        {
            foreach (var item in EnumerateIds(argument))
            {
                yield return item;
            }
        }
    }

    public static IEnumerable<string> GetNames(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var name))
        {
            foreach (var item in VariableCommandUtilities.EnumerateNames(name))
            {
                yield return item;
            }
        }

        foreach (var argument in context.Arguments)
        {
            if (TryGetId(argument, out _))
            {
                continue;
            }

            foreach (var item in VariableCommandUtilities.EnumerateNames(argument))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<int> EnumerateIds(object? value)
    {
        foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
        {
            if (TryGetId(item, out var id))
            {
                yield return id;
            }
        }
    }

    private static bool TryGetId(object? value, out int id)
    {
        var idValue = PowerShellWasmCommandUtilities.GetMemberValue(value, "Id") ?? value;
        return int.TryParse(PowerShellWasmCommandUtilities.ToInvariantString(idValue),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
