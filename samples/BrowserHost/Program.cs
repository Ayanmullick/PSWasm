using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PSWasm;

namespace PSWasm.BrowserHost;

public static partial class Interop
{
    private static readonly Dictionary<string, PowerShellWasmRuntime> s_sessions = new(StringComparer.Ordinal);

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

    [SupportedOSPlatform("browser")]
    [JSExport]
    public static string CreateSession(string environmentJson)
    {
        var environment = BrowserEnvironment.Parse(environmentJson);
        var sessionId = Guid.NewGuid().ToString("N");
        s_sessions[sessionId] = CreateRuntime(environment);
        return sessionId;
    }

    [SupportedOSPlatform("browser")]
    [JSExport]
    public static async Task<string> ExecuteSessionAsync(string sessionId, string script)
    {
        var result = await GetSession(sessionId).ExecuteAsync(script);
        return result.Text;
    }

    [SupportedOSPlatform("browser")]
    [JSExport]
    public static async Task<string> ExecuteSessionJsonAsync(string sessionId, string script)
    {
        var result = await GetSession(sessionId).ExecuteAsync(script);
        var browserResult = new BrowserPowerShellResult(result.Text, result.Records);
        return JsonSerializer.Serialize(browserResult, BrowserHostJsonContext.Default.BrowserPowerShellResult);
    }

    [SupportedOSPlatform("browser")]
    [JSExport]
    public static bool DisposeSession(string sessionId) =>
        s_sessions.Remove(sessionId);

#if PSWASM_DOM
    [SupportedOSPlatform("browser")]
    [JSExport]
    public static Task<string> InvokeDomEventJsonAsync(int registrationId, string eventJson) =>
        BrowserDomHost.InvokeEventJsonAsync(registrationId, eventJson);
#endif

    public static void Main()
    {
        Console.WriteLine("PSWasm browser host ready.");
    }

    [SupportedOSPlatform("browser")]
    private static PowerShellWasmRuntime CreateRuntime(IDictionary<string, string> environment)
    {
#if PSWASM_DOM
        return new PowerShellWasmRuntime(environment, domHost: new BrowserDomHost());
#else
        return new PowerShellWasmRuntime(environment);
#endif
    }

    private static PowerShellWasmRuntime GetSession(string sessionId)
    {
        if (s_sessions.TryGetValue(sessionId, out var runtime))
        {
            return runtime;
        }

        throw new InvalidOperationException($"PSWasm session '{sessionId}' does not exist.");
    }
}

internal sealed record BrowserPowerShellResult(string Text, IReadOnlyList<PowerShellWasmOutputRecord> Records);
