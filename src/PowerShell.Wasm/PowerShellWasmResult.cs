namespace PSWasm;

public sealed class PowerShellWasmResult(IReadOnlyList<PowerShellWasmOutputRecord> records)
{
    public IReadOnlyList<PowerShellWasmOutputRecord> Records { get; } = records;
    public IReadOnlyList<string> Output => Records.Select(FormatRecordLine).ToArray();
    public string Text => string.Join(Environment.NewLine, Output);

    private static string FormatRecordLine(PowerShellWasmOutputRecord record) =>
        record.Stream.Equals("Output", StringComparison.OrdinalIgnoreCase)
            ? record.Text
            : $"[{record.Stream}] {record.Text}";
}
