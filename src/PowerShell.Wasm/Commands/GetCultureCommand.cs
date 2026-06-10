using System.Globalization;

namespace PSWasm.Commands;

internal sealed class GetCultureCommand(bool uiCulture = false) : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
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

        context.ExecutionContext.WriteOutput(output);
        return ValueTask.CompletedTask;
    }
}
