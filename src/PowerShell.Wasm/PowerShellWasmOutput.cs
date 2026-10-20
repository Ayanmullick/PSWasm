using System.Collections;

namespace PSWasm;

// Behavioral guidance: AutomationNull.Value represents no pipeline output, separately from an explicit null item.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/AutomationNull.cs
// Browser-safe adaptation: a private marker and one-level enumeration of already-allowlisted values; no reflection.
internal static class PowerShellWasmOutput
{
    internal static object NoOutput { get; } = new NoOutputValue();

    internal static bool IsNoOutput(object? value) => ReferenceEquals(value, NoOutput);

    internal static object? AsNull(object? value) => IsNoOutput(value) ? null : value;

    // Command binding turns AutomationNull into null, including direct object-array elements.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L639-L663
    internal static object? AsCommandArgument(object? value)
    {
        if (value is not object?[] items) return AsNull(value);
        for (var index = 0; index < items.Length; index++)
        {
            if (IsNoOutput(items[index])) items[index] = null;
        }
        return items;
    }

    internal static IEnumerable<object?> Enumerate(object? value)
    {
        if (IsNoOutput(value)) yield break;
        if (value is IEnumerable values and not string and not IDictionary and not IReadOnlyDictionary<string, object?>)
        {
            foreach (var item in values)
            {
                if (!IsNoOutput(item)) yield return item;
            }
        }
        else if (value is IEnumerator enumerator)
        {
            // Explicit GetEnumerator handles are consumed in place; the dictionary itself remains one record.
            while (enumerator.MoveNext())
            {
                if (!IsNoOutput(enumerator.Current)) yield return enumerator.Current;
            }
        }
        else
        {
            yield return value;
        }
    }

    private sealed class NoOutputValue
    {
        public override string ToString() => string.Empty;
    }
}
