namespace PSWasm.Commands;

internal sealed class WriteOutputCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            context.ExecutionContext.WriteOutput(inputObject);
            return ValueTask.CompletedTask;
        }

        var output = context.Arguments.Count > 0 ? context.Arguments : context.PipelineInput;
        foreach (var argument in output)
        {
            context.ExecutionContext.WriteOutput(argument);
        }

        return ValueTask.CompletedTask;
    }
}
