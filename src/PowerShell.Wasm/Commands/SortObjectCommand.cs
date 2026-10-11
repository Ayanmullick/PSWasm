namespace PSWasm.Commands;

internal sealed class SortObjectCommand : IPowerShellWasmPipelineCommand
{
    public IReadOnlyCollection<string> SwitchParameters => ["Descending", "Unique"];

    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new Processor(context);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var processor = new Processor(context);
        foreach (var record in context.PipelineInput) await processor.ProcessAsync(record, cancellationToken);
        await processor.CompleteAsync(cancellationToken);
    }

    // Behavioral guidance: SortObjectCommand.EndProcessing resolves keys after collecting the input.
    // Browser-safe adaptation uses runtime values and allowlisted property/script expressions, without native ETS.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Sort-Object.cs#L239-L246
    private sealed class Processor : IPowerShellWasmPipelineProcessor
    {
        private readonly PowerShellWasmCommandContext _context;
        private readonly IReadOnlyList<object?> _expressions;
        private readonly bool _descending, _unique;
        private readonly List<PowerShellWasmPipelineInputItem> _input = [];

        public Processor(PowerShellWasmCommandContext context)
        {
            _context = context;
            _expressions = PipelinePropertyUtilities.GetExpressions(context);
            _descending = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Descending"));
            _unique = PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("Unique"));
        }

        public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = PowerShellWasmPipelineValue.Unwrap(record);
            if (value is null || PowerShellWasmOutput.IsNoOutput(value)) return ValueTask.CompletedTask;
            var item = new PowerShellWasmPipelineInputItem(value, PowerShellWasmPipelineValue.GetVariables(record));
            _input.Add(item);
            return ValueTask.CompletedTask;
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            // Sorting resolves keys after collecting input; grouping resolves keys on arrival.
            var items = new List<SortItem>(_input.Count);
            foreach (var item in _input)
                items.Add(new(item, await PipelinePropertyUtilities.EvaluateAsync(item, _expressions, cancellationToken)));
            var comparer = Comparer<SortItem>.Create((left, right) =>
            {
                var comparison = PipelinePropertyUtilities.Compare(left.Keys, right.Keys);
                return _descending ? -comparison : comparison;
            });
            SortItem? previous = null;
            foreach (var item in items.Order(comparer))
            {
                if (_unique && previous is not null && comparer.Compare(previous, item) == 0) continue;
                await _context.ExecutionContext.WriteOutputAsync(
                    PowerShellWasmPipelineValue.Wrap(item.Input.Value, item.Input.Variables), cancellationToken);
                previous = item;
            }
        }
    }

    private sealed record SortItem(PowerShellWasmPipelineInputItem Input, object?[] Keys);
}