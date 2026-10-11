namespace PSWasm.Commands;

internal sealed class MeasureObjectCommand : IPowerShellWasmPipelineCommand
{
    public IReadOnlyCollection<string> SwitchParameters => ["Sum", "Average", "Minimum", "Maximum"];

    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new Processor(context);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var processor = new Processor(context);
        foreach (var record in context.PipelineInput) await processor.ProcessAsync(record, cancellationToken);
        await processor.CompleteAsync(cancellationToken);
    }

    // Behavioral guidance: MeasureObjectCommand.ProcessRecord reads each value before the producer resumes.
    // Browser-safe adaptation retains the numeric statistics subset and emits its dictionary in CompleteAsync.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Measure-Object.cs#L480-L496
    private sealed class Processor : IPowerShellWasmPipelineProcessor
    {
        private readonly PowerShellWasmCommandContext _context;
        private readonly string? _propertyName;
        private readonly List<double> _numericValues = [];
        private int _count;

        public Processor(PowerShellWasmCommandContext context)
        {
            _context = context;
            var propertyNames = PowerShellWasmCommandUtilities.GetPropertyNames(context);
            _propertyName = propertyNames.Count > 0 ? propertyNames[0] : null;
        }

        public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = PowerShellWasmPipelineValue.Unwrap(record);
            if (value is null || PowerShellWasmOutput.IsNoOutput(value)) return ValueTask.CompletedTask;
            _count++;
            var measured = string.IsNullOrWhiteSpace(_propertyName)
                ? value : PowerShellWasmCommandUtilities.GetMemberValue(value, _propertyName);
            if (PowerShellWasmCommandUtilities.TryToNumber(measured, out var number)) _numericValues.Add(number);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Count"] = _count };
            if (!string.IsNullOrWhiteSpace(_propertyName)) result["Property"] = _propertyName;
            AddStatistics(result, _context, _numericValues);
            return _context.ExecutionContext.WriteOutputAsync(result, cancellationToken);
        }
    }
    private static void AddStatistics(Dictionary<string, object?> result, PowerShellWasmCommandContext context, IReadOnlyList<double> values)
    {
        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Sum")))
        {
            result["Sum"] = NormalizeNumber(values.Sum());
        }

        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Average")))
        {
            result["Average"] = values.Count == 0 ? null : NormalizeNumber(values.Average());
        }

        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Minimum")))
        {
            result["Minimum"] = values.Count == 0 ? null : NormalizeNumber(values.Min());
        }

        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Maximum")))
        {
            result["Maximum"] = values.Count == 0 ? null : NormalizeNumber(values.Max());
        }
    }

    private static object NormalizeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000000001 && value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(value)
            : value;
}
