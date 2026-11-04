using System.Runtime.CompilerServices;

namespace PSWasm.Language;

// A closed exception/type hierarchy for catch filters; no reflected loading or arbitrary construction is exposed.
// Matching adapts PowerShell's inner/wrapper search and specificity ranks, not CLR first-handler dispatch.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L1500-L1669
internal static class PowerShellWasmExceptionTypes
{
    internal const string Runtime = "System.Management.Automation.RuntimeException";
    internal const string InvalidCast = "System.Management.Automation.PSInvalidCastException";
    internal const string MethodInvocation = "System.Management.Automation.MethodInvocationException";
    internal const string CommandNotFound = "System.Management.Automation.CommandNotFoundException";
    internal const string PreferenceStop = "System.Management.Automation.ActionPreferenceStopException";
    internal const string WriteError = "Microsoft.PowerShell.Commands.WriteErrorException";

    private sealed record Classification(string Wrapper, string? Inner, bool InnerAfterWrapper = false, bool ExplicitInner = false);
    private sealed record DescriptorType(string FullName);
    private static readonly ConditionalWeakTable<Exception, Classification> Classifications = new();
    private static readonly ConditionalWeakTable<object, DescriptorType> DescriptorTypes = new();

    // PowerShell RuntimeException derives SystemException; its ETS wrappers remain separate from BCL inner exceptions.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/utils/RuntimeException.cs#L20-L21
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ExtendedTypeSystemException.cs#L13-L156
    private static readonly IReadOnlyDictionary<string, string?> Parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["System.Object"] = null, ["System.Exception"] = "System.Object",
        ["System.SystemException"] = "System.Exception", ["System.ApplicationException"] = "System.Exception",
        ["System.ArithmeticException"] = "System.SystemException", ["System.DivideByZeroException"] = "System.ArithmeticException",
        ["System.OverflowException"] = "System.ArithmeticException", ["System.ArgumentException"] = "System.SystemException",
        ["System.ArgumentNullException"] = "System.ArgumentException", ["System.ArgumentOutOfRangeException"] = "System.ArgumentException",
        ["System.FormatException"] = "System.SystemException", ["System.InvalidCastException"] = "System.SystemException",
        ["System.InvalidOperationException"] = "System.SystemException", ["System.IndexOutOfRangeException"] = "System.SystemException",
        ["System.NullReferenceException"] = "System.SystemException", ["System.NotSupportedException"] = "System.SystemException",
        ["System.NotImplementedException"] = "System.SystemException", ["System.TimeoutException"] = "System.SystemException",
        ["System.IO.IOException"] = "System.SystemException", ["System.IO.FileNotFoundException"] = "System.IO.IOException",
        ["System.Text.RegularExpressions.RegexParseException"] = "System.ArgumentException",
        [Runtime] = "System.SystemException", [InvalidCast] = "System.InvalidCastException",
        ["System.Management.Automation.ExtendedTypeSystemException"] = Runtime,
        ["System.Management.Automation.MethodException"] = "System.Management.Automation.ExtendedTypeSystemException",
        [MethodInvocation] = "System.Management.Automation.MethodException", [CommandNotFound] = Runtime,
        [PreferenceStop] = Runtime, [WriteError] = "System.SystemException",
        ["System.Management.Automation.ParameterBindingException"] = Runtime,
        ["System.Management.Automation.ArgumentTransformationMetadataException"] = "System.Management.Automation.MetadataException",
        ["System.Management.Automation.MetadataException"] = Runtime,
        ["System.Management.Automation.PSArgumentException"] = "System.ArgumentException",
        ["System.Management.Automation.PSInvalidOperationException"] = "System.InvalidOperationException"
    };

    internal static bool TryResolveExceptionName(string typeName, out string fullName)
    {
        var name = typeName.Trim();
        if (Parents.TryGetValue(name, out _)) { fullName = Parents.Keys.First(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)); return true; }
        if (!name.Contains('.') && Parents.TryGetValue("System." + name, out _))
        {
            fullName = Parents.Keys.First(key => key.Equals("System." + name, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        // Native type lookup adds System, but does not add System.Management.Automation for short exception names.
        if (name.StartsWith("Management.Automation.", StringComparison.OrdinalIgnoreCase) && Parents.ContainsKey("System." + name))
        {
            fullName = Parents.Keys.First(key => key.Equals("System." + name, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        fullName = string.Empty;
        return false;
    }

    internal static bool TryResolve(string typeName, out string fullName)
    {
        try
        {
            if (TryResolveExceptionName(typeName, out fullName) ||
                PowerShellWasmDotNetBridge.TryResolveTypeName(typeName, out fullName)) return true;
        }
        catch (InvalidOperationException)
        {
            // Unsupported generic arguments are unresolved filters, not eager failures in an unused catch.
            fullName = string.Empty;
            return false;
        }
        // Known interface filters are permitted but never match: native dispatch uses subclassing, not interface assignability.
        foreach (var known in new[] { "System.Runtime.Serialization.ISerializable", "System.IComparable", "System.IConvertible",
            "System.Collections.IDictionary", "System.Collections.IEnumerable", "System.Collections.IEnumerator" })
        {
            if (known.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                known.Equals("System." + typeName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                fullName = known;
                return true;
            }
        }
        fullName = string.Empty;
        return false;
    }

    internal static bool IsAssignableFrom(string baseType, string derivedType)
    {
        if (baseType.Equals(derivedType, StringComparison.OrdinalIgnoreCase)) return true;
        var current = derivedType;
        while (Parents.TryGetValue(current, out var parent) && parent is not null)
        {
            if (baseType.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
            current = parent;
        }
        // Known nonexception filters are legal. Object is a class base; interfaces never match native IsSubclassOf.
        if (baseType == "System.Object") return current != "System.Object" && !IsInterface(current);
        if (baseType == "System.Array") return derivedType.EndsWith("[]", StringComparison.Ordinal);
        return false;
    }

    private static bool IsInterface(string name) => name is "System.Collections.IDictionary" or "System.Collections.IEnumerable"
        or "System.Collections.IEnumerator" or "System.IComparable" or "System.IConvertible" or "System.Runtime.Serialization.ISerializable";

    internal static InvalidOperationException Create(string typeName, string message, Exception? inner = null, string? innerType = null)
    {
        var error = new InvalidOperationException(message, inner);
        Classifications.Add(error, new(typeName, innerType));
        return error;
    }

    internal static Exception CastFailure(Exception error) =>
        Create(Runtime, error.Message, error, InvalidCast);

    internal static Exception MethodFailure(Exception error, bool fromClrMember = false)
    {
        var actualType = error.GetType().FullName;
        var innerType = fromClrMember && actualType is not null && Parents.ContainsKey(actualType)
            ? actualType : Describe(error).Inner ?? Describe(error).Wrapper;
        return Create(MethodInvocation, error.Message, error, innerType);
    }

    internal static void ClassifyPreferenceStop(Exception error, Exception? original = null)
    {
        Classifications.Remove(error);
        Classifications.Add(error, new(PreferenceStop, original is null ? null : Describe(original).Inner ?? Describe(original).Wrapper,
            InnerAfterWrapper: true));
    }

    internal static void CopyClassification(Exception source, Exception target) => Classifications.Add(target, Describe(source));

    internal static void ClassifyExplicitThrow(Exception error, string exceptionType) =>
        Classifications.Add(error, new(Runtime, exceptionType, ExplicitInner: true));

    internal static string DisplayType(Exception error)
    {
        var info = Describe(error);
        return info.ExplicitInner ? info.Inner! : info.Wrapper;
    }

    internal static bool ReplacesCatchDescriptor(Exception error) => Describe(error) is { Inner: not null, InnerAfterWrapper: false, ExplicitInner: false };

    internal static void RegisterDescriptor(object descriptor, string typeName) => DescriptorTypes.Add(descriptor, new(typeName));

    internal static bool TryGetDescriptorType(object? descriptor, out string fullName)
    {
        if (descriptor is not null && DescriptorTypes.TryGetValue(descriptor, out var type)) { fullName = type.FullName; return true; }
        fullName = string.Empty;
        return false;
    }

    private static Classification Describe(Exception error)
    {
        if (Classifications.TryGetValue(error, out var classification)) return classification;
        // Synthetic language failures are represented internally by InvalidOperationException, not exposed as that CLR type.
        if (error is InvalidOperationException) return new(Runtime, null);
        var fullName = error.GetType().FullName;
        return fullName is not null && Parents.ContainsKey(fullName) ? new(Runtime, fullName) : new(Runtime, null);
    }

    internal static int FindHandler(Exception error, IReadOnlyList<CatchClauseAst> clauses, out string? selectedType)
    {
        // Resolve every filter only after an exception occurs, including filters after an otherwise matching handler.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L5331-L5438
        var filters = new List<(string? Type, int Clause)>();
        for (var clause = 0; clause < clauses.Count; clause++)
        {
            if (clauses[clause].TypeNames.Count == 0) filters.Add((null, clause));
            foreach (var name in clauses[clause].TypeNames)
            {
                if (!TryResolve(name, out var type)) throw Create(Runtime, $"Unable to find type [{name}].");
                filters.Add((type, clause));
            }
        }
        var ranks = new int[filters.Count];
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Type is null) { ranks[i] = filters.Count - 1; continue; }
            for (var j = i + 1; j < filters.Count; j++)
                if (filters[j].Type is { } parent && parent != filters[i].Type && IsAssignableFrom(parent, filters[i].Type!)) ranks[j]++;
        }
        var best = -1;
        var bestRank = int.MaxValue;
        selectedType = null;
        var info = Describe(error);
        var candidates = info.Inner is null ? new[] { info.Wrapper }
            : info.InnerAfterWrapper ? new[] { info.Wrapper, info.Inner } : new[] { info.Inner, info.Wrapper };
        foreach (var candidate in candidates)
        {
            var match = filters.FindIndex(filter => filter.Type == candidate);
            if (match < 0) match = filters.FindIndex(filter => filter.Type is not null && IsAssignableFrom(filter.Type, candidate));
            if (match < 0) match = filters.FindIndex(filter => filter.Type is null);
            if (match >= 0 && (ranks[match] < bestRank || ranks[match] == bestRank && filters[best].Type is null))
            {
                best = match;
                bestRank = ranks[match];
                selectedType = candidate;
            }
            if (bestRank == 0) break;
        }
        return best < 0 ? -1 : filters[best].Clause;
    }
}
