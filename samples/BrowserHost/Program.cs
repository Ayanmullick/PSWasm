using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PSWasm;

namespace PSWasm.BrowserHost;

public static partial class Interop
{
    [SupportedOSPlatform("browser")]
    [JSExport]
    public static async Task<string> ExecuteAsync(string script, string environmentJson)
    {
        var environment = BrowserEnvironment.Parse(environmentJson);
        var runtime = CreateRuntime(environment);
        var result = await runtime.ExecuteAsync(script);
        return result.Text;
    }

    [SupportedOSPlatform("browser")]
    [JSExport]
    public static async Task<string> ExecuteJsonAsync(string script, string environmentJson)
    {
        var environment = BrowserEnvironment.Parse(environmentJson);
        var runtime = CreateRuntime(environment);
        var result = await runtime.ExecuteAsync(script);
        var browserResult = new BrowserPowerShellResult(result.Text, result.Records);
        return JsonSerializer.Serialize(browserResult, BrowserHostJsonContext.Default.BrowserPowerShellResult);
    }

    public static void Main()
    {
        Console.WriteLine("PSWasm browser host ready.");
    }

    private static PowerShellWasmRuntime CreateRuntime(IDictionary<string, string> environment)
    {
        return new PowerShellWasmRuntime(environment);
    }
}

internal sealed record BrowserPowerShellResult(string Text, IReadOnlyList<PowerShellWasmOutputRecord> Records);
