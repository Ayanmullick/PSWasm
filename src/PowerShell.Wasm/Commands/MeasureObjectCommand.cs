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
        private readonly PowerShellWasmScriptBlock? _propertyScript;
        private readonly List<double> _numericValues = [];
        private int _count, _nullValues;

        public Processor(PowerShellWasmCommandContext context)
        {
            _context = context;
            var property = PipelinePropertyUtilities.GetExpressions(context).FirstOrDefault();
            _propertyScript = property as PowerShellWasmScriptBlock;
            _propertyName = _propertyScript is not null
                ? _propertyScript.SourceText ?? string.Empty
                : property is null ? null : PowerShellWasmCommandUtilities.ToInvariantString(property);
        }

        public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = PowerShellWasmPipelineValue.Unwrap(record);
            if (value is null || PowerShellWasmOutput.IsNoOutput(value)) return;
            _count++;
            object? measured;
            if (_propertyScript is not null)
            {
                var output = await _propertyScript.InvokeCallbackAsync(value, null, PowerShellWasmPipelineValue.GetVariables(record),
                    PowerShellWasmScriptBlockInvocation.CalculatedProperty, cancellationToken);
                // A property expression produces one measurement per input, including an empty or multi-value result.
                // No output converts to zero; explicit null contributes to Sum/Average but not Minimum/Maximum.
                measured = output.Count switch { 0 => 0, 1 => output[0], _ => output.ToArray() };
                if (PowerShellWasmOutput.AsNull(measured) is null) _nullValues++;
            }
            else
            {
                measured = string.IsNullOrWhiteSpace(_propertyName)
                    ? value : PowerShellWasmCommandUtilities.GetMemberValue(value, _propertyName);
            }
            if (PowerShellWasmCommandUtilities.TryToNumber(measured, out var number)) _numericValues.Add(number);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Count"] = _count };
            if (_propertyScript is not null || !string.IsNullOrWhiteSpace(_propertyName)) result["Property"] = _propertyName;
            AddStatistics(result, _context, _numericValues, _nullValues);
            return _context.ExecutionContext.WriteOutputAsync(result, cancellationToken);
        }
    }
    private static void AddStatistics(Dictionary<string, object?> result, PowerShellWasmCommandContext context,
        IReadOnlyList<double> values, int nullValues)
    {
        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Sum")))
        {
            result["Sum"] = NormalizeNumber(values.Sum());
        }

        if (PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Average")))
        {
            var numericCount = values.Count + nullValues;
            result["Average"] = numericCount == 0 ? null : NormalizeNumber(values.Sum() / numericCount);
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
