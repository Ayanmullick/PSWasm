using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PSWasm;

namespace PSWasm.BrowserHost;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserAzureAuthHost : IPowerShellWasmAzureAuthHost
{
    public async ValueTask<IReadOnlyDictionary<string, object?>> ConnectAsync(
        PowerShellWasmAzureAuthConnectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new BrowserAzureAuthConnectOptions(request.Tenant, request.ClientId, request.Scopes.ToArray());
        var json = JsonSerializer.Serialize(options, BrowserHostJsonContext.Default.BrowserAzureAuthConnectOptions);
        return ToAccountRecord(await DeserializeAccountAsync(ConnectJsonAsync(json)));
    }

    public async ValueTask<IReadOnlyDictionary<string, object?>> GetContextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ToAccountRecord(await DeserializeAccountAsync(GetContextJsonAsync()));
    }

    public async ValueTask<IReadOnlyDictionary<string, object?>> GetAccessTokenAsync(
        PowerShellWasmAzureAuthTokenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new BrowserAzureAuthTokenOptions(request.ResourceUrl, request.Scopes.ToArray());
        var json = JsonSerializer.Serialize(options, BrowserHostJsonContext.Default.BrowserAzureAuthTokenOptions);
        var token = await DeserializeTokenAsync(GetAccessTokenJsonAsync(json));

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Token"] = token.AccessToken,
            ["ExpiresOn"] = token.ExpiresOn,
            ["TenantId"] = token.TenantId,
            ["UserId"] = token.UserId,
            ["Account"] = token.Account,
            ["ResourceUrl"] = request.ResourceUrl,
            ["Scopes"] = request.Scopes.ToArray(),
            ["TokenType"] = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
            ["AuthType"] = "User",
            ["ContextType"] = "Browser"
        };
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DisconnectJsonAsync();
    }

    private static async Task<BrowserAzureAuthAccount> DeserializeAccountAsync(Task<string> jsonTask)
    {
        var json = await jsonTask;
        return JsonSerializer.Deserialize(json, BrowserHostJsonContext.Default.BrowserAzureAuthAccount) ??
            new BrowserAzureAuthAccount(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static async Task<BrowserAzureAuthAccessToken> DeserializeTokenAsync(Task<string> jsonTask)
    {
        var json = await jsonTask;
        return JsonSerializer.Deserialize(json, BrowserHostJsonContext.Default.BrowserAzureAuthAccessToken) ??
            throw new InvalidOperationException("Browser authentication did not return an access token.");
    }

    private static Dictionary<string, object?> ToAccountRecord(BrowserAzureAuthAccount account) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Account"] = account.UserName,
            ["UserName"] = account.UserName,
            ["Name"] = account.Name,
            ["TenantId"] = account.TenantId,
            ["UserId"] = account.UserId,
            ["ClientId"] = account.ClientId,
            ["Authenticated"] = account.Authenticated,
            ["AuthType"] = "User",
            ["ContextType"] = "Browser"
        };

    [JSImport("globalThis.pswasmAzureAuth.connect")]
    private static partial Task<string> ConnectJsonAsync(string optionsJson);

    [JSImport("globalThis.pswasmAzureAuth.getContext")]
    private static partial Task<string> GetContextJsonAsync();

    [JSImport("globalThis.pswasmAzureAuth.getAccessToken")]
    private static partial Task<string> GetAccessTokenJsonAsync(string optionsJson);

    [JSImport("globalThis.pswasmAzureAuth.disconnect")]
    private static partial Task<string> DisconnectJsonAsync();
}
