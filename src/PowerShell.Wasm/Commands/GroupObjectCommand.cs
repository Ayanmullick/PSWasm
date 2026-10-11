namespace PSWasm.Commands;

internal sealed class GroupObjectCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new Processor(context);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var processor = new Processor(context);
        foreach (var record in context.PipelineInput) await processor.ProcessAsync(record, cancellationToken);
        await processor.CompleteAsync(cancellationToken);
    }

    // Behavioral guidance: GroupObjectCommand.ProcessRecord evaluates keys as records arrive; EndProcessing emits groups.
    // Browser-safe adaptation retains runtime dictionaries and the supported property/script expression subset.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Group-Object.cs#L387-L407
    private sealed class Processor : IPowerShellWasmPipelineProcessor
    {
        private readonly PowerShellWasmCommandContext _context;
        private readonly IReadOnlyList<object?> _expressions;
        private readonly Dictionary<string, GroupBucket> _groups = new(StringComparer.OrdinalIgnoreCase);

        public Processor(PowerShellWasmCommandContext context)
        {
            _context = context;
            _expressions = PipelinePropertyUtilities.GetExpressions(context);
        }

        public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = PowerShellWasmPipelineValue.Unwrap(record);
            if (value is null || PowerShellWasmOutput.IsNoOutput(value)) return;
            var item = new PowerShellWasmPipelineInputItem(value, PowerShellWasmPipelineValue.GetVariables(record));
            var keys = await PipelinePropertyUtilities.EvaluateAsync(item, _expressions, cancellationToken);
            var name = string.Join(", ", keys.Select(PowerShellWasmCommandUtilities.FormatValue));
            if (!_groups.TryGetValue(name, out var group))
            {
                group = new(name, keys);
                _groups[name] = group;
            }
            group.Items.Add(value);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            var comparer = Comparer<GroupBucket>.Create((left, right) => PipelinePropertyUtilities.Compare(left.Values, right.Values));
            foreach (var group in _groups.Values.Order(comparer))
                await _context.ExecutionContext.WriteOutputAsync(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Count"] = group.Items.Count,
                    ["Name"] = group.Name,
                    ["Group"] = group.Items.ToArray(),
                    ["Values"] = group.Values
                }, cancellationToken);
        }
    }

    private sealed record GroupBucket(string Name, object?[] Values)
    {
        public List<object?> Items { get; } = [];
    }
}