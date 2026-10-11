namespace PSWasm.Commands;

internal sealed class ThrowCommand : IPowerShellWasmPipelineCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = context.Arguments.Count > 0 ? context.Arguments : context.PipelineInput;
        var message = input.Count == 0
            ? "ScriptHalted"
            : string.Join(" ", input.Select(PowerShellWasmCommandUtilities.ToInvariantString));

        throw new InvalidOperationException(message);
    }
}
