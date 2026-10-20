namespace PSWasm;

public interface IPowerShellWasmCommand
{
    IReadOnlyCollection<string> SwitchParameters => [];

    ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken);
}

// Runtime-owned commands use an awaited, invocation-scoped lifecycle. Public delegate commands retain InvokeAsync.
internal interface IPowerShellWasmPipelineCommand : IPowerShellWasmCommand
{
    IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);
}

internal interface IPowerShellWasmPipelineProcessor
{
    bool IsInputComplete => false;
    ValueTask BeginAsync(CancellationToken cancellationToken);
    ValueTask ProcessAsync(object? record, CancellationToken cancellationToken);
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

// Source production starts during completion, after downstream Begin callbacks are ready.
internal sealed class PowerShellWasmPerRecordProcessor(
    IPowerShellWasmCommand command, PowerShellWasmCommandContext context, bool hasPipelineInput)
    : IPowerShellWasmPipelineProcessor
{
    public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ProcessAsync(object? record, CancellationToken cancellationToken) =>
        command.InvokeAsync(context.WithPipelineInput([record]), cancellationToken);

    public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
        hasPipelineInput ? ValueTask.CompletedTask : command.InvokeAsync(context, cancellationToken);
}

// Converters that require the complete input retain one invocation, rather than resetting state for every record.
internal sealed class PowerShellWasmBufferedProcessor(
    IPowerShellWasmCommand command, PowerShellWasmCommandContext context, bool hasPipelineInput)
    : IPowerShellWasmPipelineProcessor
{
    private readonly List<object?> _input = [];

    public ValueTask BeginAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _input.Add(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
        command.InvokeAsync(hasPipelineInput ? context.WithPipelineInput(_input) : context, cancellationToken);
}
