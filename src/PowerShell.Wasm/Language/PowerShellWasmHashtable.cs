using System.Collections;

namespace PSWasm.Language;

// Behavioral guidance: hash literals retain raw keys and compare string keys without case sensitivity.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6520-L6536
// Use the platform dictionary directly; unlike a string-keyed adapter, numeric and string keys stay distinct.
internal sealed class PowerShellWasmHashtable : Hashtable
{
    public PowerShellWasmHashtable()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public override string ToString() => "System.Collections.Hashtable";
}
