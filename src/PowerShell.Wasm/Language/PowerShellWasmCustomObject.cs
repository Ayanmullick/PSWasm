namespace PSWasm.Language;

// Behavioral guidance: PSCustomObjectConverter creates a custom object from a hashtable instead of retaining its type.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L1806-L1825
// Browser-safe adaptation: a distinct case-insensitive property bag retaining existing member/index access, without ETS.
internal sealed class PowerShellWasmCustomObject : Dictionary<string, object?>
{
    public PowerShellWasmCustomObject(IDictionary<string, object?> properties)
        : base(properties, StringComparer.OrdinalIgnoreCase)
    {
    }
}
