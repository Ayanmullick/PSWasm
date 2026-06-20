namespace PSWasm.Language;

internal sealed class PowerShellWasmHashtable : Dictionary<string, object?>
{
    public PowerShellWasmHashtable()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}
