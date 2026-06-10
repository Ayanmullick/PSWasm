using System.Globalization;

namespace PSWasm.Commands;

internal sealed class ThrowCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = context.Arguments.Count > 0 ? context.Arguments : context.PipelineInput;
        var message = input.Count == 0
            ? "ScriptHalted"
            : string.Join(" ", input.Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture)));

        throw new InvalidOperationException(message);
    }
}
