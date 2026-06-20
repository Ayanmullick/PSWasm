using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSWasm.BrowserHost;

internal static class BrowserEnvironment
{
    public static Dictionary<string, string> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = JsonSerializer.Deserialize(json, BrowserHostJsonContext.Default.DictionaryStringString);
        return values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(BrowserPowerShellResult))]
[JsonSerializable(typeof(PSWasm.PowerShellWasmOutputRecord))]
[JsonSerializable(typeof(BrowserAzureAuthConnectOptions))]
[JsonSerializable(typeof(BrowserAzureAuthTokenOptions))]
[JsonSerializable(typeof(BrowserAzureAuthAccount))]
[JsonSerializable(typeof(BrowserAzureAuthAccessToken))]
internal sealed partial class BrowserHostJsonContext : JsonSerializerContext;

internal sealed record BrowserAzureAuthConnectOptions(string Tenant, string ClientId, string[] Scopes);

internal sealed record BrowserAzureAuthTokenOptions(string ResourceUrl, string[] Scopes);

internal sealed record BrowserAzureAuthAccount(
    bool Authenticated,
    string UserName,
    string Name,
    string TenantId,
    string UserId,
    string ClientId);

internal sealed record BrowserAzureAuthAccessToken(
    string AccessToken,
    string ExpiresOn,
    string TenantId,
    string UserId,
    string Account,
    string TokenType);
