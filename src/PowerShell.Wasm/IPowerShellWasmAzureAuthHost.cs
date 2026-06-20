namespace PSWasm;

public interface IPowerShellWasmAzureAuthHost
{
    ValueTask<IReadOnlyDictionary<string, object?>> ConnectAsync(
        PowerShellWasmAzureAuthConnectRequest request,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, object?>> GetContextAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, object?>> GetAccessTokenAsync(
        PowerShellWasmAzureAuthTokenRequest request,
        CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}

public sealed record PowerShellWasmAzureAuthConnectRequest(
    string Tenant,
    string ClientId,
    IReadOnlyList<string> Scopes);

public sealed record PowerShellWasmAzureAuthTokenRequest(
    string ResourceUrl,
    IReadOnlyList<string> Scopes);
