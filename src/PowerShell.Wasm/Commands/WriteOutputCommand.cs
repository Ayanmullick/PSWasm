namespace PSWasm.Commands;

internal sealed class WriteOutputCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        context.Arguments.Count == 0 && !context.Parameters.ContainsKey("InputObject")
            ? new PowerShellWasmPerRecordProcessor(this, context, hasPipelineInput)
            : new PowerShellWasmBufferedProcessor(this, context, hasPipelineInput);

    public IReadOnlyCollection<string> SwitchParameters => ["NoEnumerate"];

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var noEnumerate = context.Parameters.TryGetValue("NoEnumerate", out var switchValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(switchValue);
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            await WriteAsync(context, PowerShellWasmOutput.AsNull(inputObject), noEnumerate, cancellationToken);
            return;
        }

        if (context.Arguments.Count > 0)
        {
            // Remaining arguments bind as a collection; one supplied collection retains its original shape.
            var first = PowerShellWasmOutput.AsNull(context.Arguments[0]);
            var input = context.Arguments.Count == 1 && first is System.Collections.IEnumerable &&
                first is not string and not System.Collections.IDictionary and not IReadOnlyDictionary<string, object?>
                ? first : context.Arguments.Select(PowerShellWasmOutput.AsNull).ToList();
            await WriteAsync(context, input, noEnumerate, cancellationToken);
            return;
        }

        foreach (var item in context.PipelineInput)
        {
            await WriteAsync(context, item, noEnumerate, cancellationToken);
        }

        return;
    }

    // Behavioral guidance: WriteOutputCommand.ProcessRecord explicitly controls one-level collection enumeration.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/Microsoft.PowerShell.Commands.Utility/commands/utility/Write-Object.cs#L32-L41
    // Browser-safe adaptation: use runtime values and pipeline metadata instead of PSObject and the native command binder.
    private static async ValueTask WriteAsync(PowerShellWasmCommandContext context, object? input, bool noEnumerate, CancellationToken cancellationToken)
    {
        if (noEnumerate)
        {
            await context.ExecutionContext.WriteOutputAsync(input, cancellationToken);
            return;
        }

        var variables = PowerShellWasmPipelineValue.GetVariables(input);
        foreach (var value in PowerShellWasmOutput.Enumerate(PowerShellWasmPipelineValue.Unwrap(input)))
        {
            await context.ExecutionContext.WriteOutputAsync(PowerShellWasmPipelineValue.Wrap(value, variables), cancellationToken);
        }
    }
}
