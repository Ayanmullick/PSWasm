namespace PSWasm.Commands;

internal sealed class CallOperatorCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("The call operator requires a script block in this browser runtime.");
        }

        if (context.Arguments[0] is not PowerShellWasmScriptBlock scriptBlock)
        {
            throw new InvalidOperationException("The browser-safe call operator can invoke script blocks only.");
        }

        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput).ToArray()
        };
        var args = context.Arguments.Skip(1).ToArray();

        foreach (var output in await scriptBlock.InvokeAsync(null, args, variables, cancellationToken))
        {
            context.ExecutionContext.WriteOutput(output);
        }
    }
}
