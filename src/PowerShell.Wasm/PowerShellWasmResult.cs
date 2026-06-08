namespace PSWasm;

public sealed class PowerShellWasmResult(IReadOnlyList<string> output)
{
    public IReadOnlyList<string> Output { get; } = output;
    public string Text => string.Join(Environment.NewLine, Output);
}
