namespace PSWasm.Commands;

internal sealed class WriteOutputCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        foreach (var argument in context.Arguments)
        {
            context.ExecutionContext.WriteOutput(argument);
        }

        return ValueTask.CompletedTask;
    }
}
