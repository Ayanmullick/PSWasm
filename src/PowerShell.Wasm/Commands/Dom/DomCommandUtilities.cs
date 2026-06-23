using System.Globalization;

namespace PSWasm.Commands;

internal static class DomCommandUtilities
{
    public static IPowerShellWasmDomHost GetDomHost(PowerShellWasmCommandContext context) =>
        context.ExecutionContext.DomHost ??
        throw new InvalidOperationException("DOM interaction commands require a browser DOM host.");

    public static IReadOnlyList<string> GetSelectors(PowerShellWasmCommandContext context, params string[] parameterNames)
    {
        var selectors = new List<string>();
        foreach (var name in parameterNames.Prepend("Selector"))
        {
            if (context.Parameters.TryGetValue(name, out var parameterValue))
            {
                AddValues(parameterValue);
            }
        }

        foreach (var argument in context.Arguments)
        {
            if (argument is PowerShellWasmScriptBlock)
            {
                continue;
            }

            AddValues(argument);
        }

        return selectors.Where(static selector => !string.IsNullOrWhiteSpace(selector)).ToArray();

        void AddValues(object? value)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
            {
                selectors.Add(PowerShellWasmCommandUtilities.ToInvariantString(item));
            }
        }
    }

    public static string GetRequiredText(PowerShellWasmCommandContext context, string parameterName, int argumentIndex)
    {
        if (context.Parameters.TryGetValue(parameterName, out var parameterValue))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(parameterValue);
        }

        if (context.Arguments.Count > argumentIndex)
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[argumentIndex]);
        }

        throw new InvalidOperationException($"{parameterName} is required.");
    }

    public static object? GetRequiredValue(PowerShellWasmCommandContext context, string parameterName, int argumentIndex)
    {
        if (context.Parameters.TryGetValue(parameterName, out var parameterValue))
        {
            return parameterValue;
        }

        if (context.Arguments.Count > argumentIndex)
        {
            return context.Arguments[argumentIndex];
        }

        throw new InvalidOperationException($"{parameterName} is required.");
    }

    public static string GetStorage(PowerShellWasmCommandContext context)
    {
        var storage = context.Parameters.TryGetValue("Storage", out var value)
            ? PowerShellWasmCommandUtilities.ToInvariantString(value)
            : "Local";

        return storage.Equals("Session", StringComparison.OrdinalIgnoreCase)
            ? "Session"
            : storage.Equals("Local", StringComparison.OrdinalIgnoreCase)
                ? "Local"
                : throw new InvalidOperationException("Storage must be Local or Session.");
    }

    public static IReadOnlyList<string> GetKeys(PowerShellWasmCommandContext context)
    {
        var keys = new List<string>();
        if (context.Parameters.TryGetValue("Key", out var key))
        {
            AddValues(key);
        }

        foreach (var argument in context.Arguments)
        {
            AddValues(argument);
        }

        return keys.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray();

        void AddValues(object? value)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
            {
                keys.Add(PowerShellWasmCommandUtilities.ToInvariantString(item));
            }
        }
    }

    public static IReadOnlyList<int> GetIds(PowerShellWasmCommandContext context)
    {
        var ids = new List<int>();
        if (context.Parameters.TryGetValue("Id", out var id))
        {
            AddValues(id);
        }

        foreach (var argument in context.Arguments)
        {
            AddValues(argument);
        }

        return ids.ToArray();

        void AddValues(object? value)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumerateInput([value]))
            {
                var idValue = PowerShellWasmCommandUtilities.GetMemberValue(item, "Id") ?? item;
                if (int.TryParse(
                    PowerShellWasmCommandUtilities.ToInvariantString(idValue),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed))
                {
                    ids.Add(parsed);
                }
            }
        }
    }
}

internal static class DomSessionCommandUtilities
{
    public static IEnumerable<Dictionary<string, object?>> SelectSessions(
        PowerShellWasmCommandContext context,
        bool requireSelection)
    {
        var sessions = context.ExecutionContext.GetDomSessions();
        var selected = SelectSessionsCore(context, sessions).ToArray();
        if (requireSelection && selected.Length == 0)
        {
            throw new InvalidOperationException("An existing DOM session name, id, or session object is required.");
        }

        return selected;
    }

    public static Dictionary<string, object?>? GetSession(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Session", out var session))
        {
            if (TryGetSessionRecord(session, out var sessionRecord))
            {
                return sessionRecord;
            }

            if (TryGetId(session, out var id))
            {
                return context.ExecutionContext.GetDomSessions().FirstOrDefault(item =>
                    Convert.ToInt32(item["Id"], CultureInfo.InvariantCulture) == id);
            }

            var sessionName = PowerShellWasmCommandUtilities.ToInvariantString(session);
            return context.ExecutionContext.GetDomSessions().FirstOrDefault(item =>
                VariableCommandUtilities.NameMatches(PowerShellWasmCommandUtilities.ToInvariantString(item["Name"]), sessionName));
        }

        return context.ExecutionContext.GetDomSessions().FirstOrDefault();
    }

    private static IEnumerable<Dictionary<string, object?>> SelectSessionsCore(
        PowerShellWasmCommandContext context,
        IReadOnlyList<Dictionary<string, object?>> sessions)
    {
        var ids = GetIds(context).ToArray();
        if (ids.Length > 0)
        {
            return sessions.Where(session => ids.Contains(Convert.ToInt32(session["Id"], CultureInfo.InvariantCulture)));
        }

        var names = GetNames(context).ToArray();
        return names.Length == 0 ? sessions : sessions.Where(session =>
            names.Any(name => VariableCommandUtilities.NameMatches(PowerShellWasmCommandUtilities.ToInvariantString(session["Name"]), name)));
    }

    private static IEnumerable<int> GetIds(PowerShellWasmCommandContext context)
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

    private static IEnumerable<string> GetNames(PowerShellWasmCommandContext context)
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
            if (TryGetId(argument, out _) || TryGetSessionRecord(argument, out _))
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
            NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool TryGetSessionRecord(object? value, out Dictionary<string, object?> session)
    {
        session = [];
        if (value is not Dictionary<string, object?> dictionary)
        {
            return false;
        }

        if (!dictionary.ContainsKey("SessionType") ||
            !PowerShellWasmCommandUtilities.ToInvariantString(dictionary["SessionType"]).Equals("Dom", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        session = new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
        return true;
    }
}
