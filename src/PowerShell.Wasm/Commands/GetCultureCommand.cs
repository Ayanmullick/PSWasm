using System.Globalization;

namespace PSWasm.Commands;

internal sealed class GetCultureCommand(bool uiCulture = false) : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var culture = uiCulture ? CultureInfo.CurrentUICulture : CultureInfo.CurrentCulture;
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = culture.Name,
            ["DisplayName"] = culture.DisplayName,
            ["EnglishName"] = culture.EnglishName,
            ["NativeName"] = culture.NativeName,
            ["TwoLetterISOLanguageName"] = culture.TwoLetterISOLanguageName,
            ["LCID"] = culture.LCID
        };

        await context.ExecutionContext.WriteOutputAsync(output, cancellationToken);
        return;
    }
}
