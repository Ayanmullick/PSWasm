namespace PSWasm.Commands;

// Browser-safe subset modeled after Az.Accounts command surfaces:
// - Connect-AzAccount:
//   https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/Account/ConnectAzureRmAccount.cs
// - Get-AzAccessToken:
//   https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/Token/GetAzureRmAccessToken.cs
// - Get-AzContext:
//   https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/Context/GetAzureRMContext.cs
// - Disconnect-AzAccount:
//   https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/Account/DisconnectAzureRmAccount.cs
// The desktop implementation owns encrypted profile/token persistence through Az.Accounts.
// PSWasm delegates the equivalent browser-safe account recovery to the browser auth host.
internal sealed class ConnectAzAccountCommand(IPowerShellWasmAzureAuthHost? authHost) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        AzureAuthCommandUtilities.ThrowIfUnsupportedParameters(
            context,
            "Identity",
            "ServicePrincipal",
            "Credential",
            "CertificateThumbprint",
            "UseDeviceAuthentication");
        var host = AzureAuthCommandUtilities.GetAuthHost(authHost);
        var clientId = AzureAuthCommandUtilities.GetRequiredText(context, "ClientId", 0);
        var tenant = AzureAuthCommandUtilities.GetText(context, "Tenant", "TenantId") ??
            AzureAuthCommandUtilities.GetArgumentText(context, 1) ??
            "organizations";
        var scopes = AzureAuthCommandUtilities.GetScopes(context);
        var result = await host.ConnectAsync(new PowerShellWasmAzureAuthConnectRequest(tenant, clientId, scopes), cancellationToken);
        context.ExecutionContext.WriteOutput(AzureAuthCommandUtilities.CloneRecord(result));
    }
}

internal sealed class GetAzAccessTokenCommand(IPowerShellWasmAzureAuthHost? authHost) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        AzureAuthCommandUtilities.ThrowIfUnsupportedParameters(context, "AsSecureString");
        var host = AzureAuthCommandUtilities.GetAuthHost(authHost);
        var explicitResourceUrl = AzureAuthCommandUtilities.GetText(context, "ResourceUrl", "Resource", "ResourceUri") ??
            AzureAuthCommandUtilities.GetArgumentText(context, 0);
        var scopes = AzureAuthCommandUtilities.GetScopes(context);
        var resourceUrl = explicitResourceUrl ?? (scopes.Count == 0 ? "https://management.azure.com/" : string.Empty);
        if (scopes.Count == 0)
        {
            scopes = [AzureAuthCommandUtilities.GetUserImpersonationScope(resourceUrl)];
        }

        var result = await host.GetAccessTokenAsync(new PowerShellWasmAzureAuthTokenRequest(resourceUrl, scopes), cancellationToken);
        context.ExecutionContext.WriteOutput(AzureAuthCommandUtilities.CloneRecord(result));
    }
}

internal sealed class GetAzContextCommand(IPowerShellWasmAzureAuthHost? authHost) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = AzureAuthCommandUtilities.GetAuthHost(authHost);
        context.ExecutionContext.WriteOutput(AzureAuthCommandUtilities.CloneRecord(await host.GetContextAsync(cancellationToken)));
    }
}

internal sealed class DisconnectAzAccountCommand(IPowerShellWasmAzureAuthHost? authHost) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = AzureAuthCommandUtilities.GetAuthHost(authHost);
        await host.DisconnectAsync(cancellationToken);
    }
}

internal static class AzureAuthCommandUtilities
{
    public static IPowerShellWasmAzureAuthHost GetAuthHost(IPowerShellWasmAzureAuthHost? authHost) =>
        authHost ?? throw new InvalidOperationException(
            "Browser-safe Azure authentication is not available in this host.");

    public static string GetRequiredText(PowerShellWasmCommandContext context, string parameterName, int argumentIndex)
    {
        var text = GetText(context, parameterName) ?? GetArgumentText(context, argumentIndex);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{parameterName} is required.");
        }

        return text;
    }

    public static string? GetText(PowerShellWasmCommandContext context, params string[] names)
    {
        foreach (var name in names)
        {
            if (context.Parameters.TryGetValue(name, out var value))
            {
                return PowerShellWasmCommandUtilities.ToInvariantString(value);
            }
        }

        return null;
    }

    public static string? GetArgumentText(PowerShellWasmCommandContext context, int index) =>
        context.Arguments.Count > index ? PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[index]) : null;

    public static IReadOnlyList<string> GetScopes(PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Scope", out var scope) &&
            !context.Parameters.TryGetValue("Scopes", out scope))
        {
            return [];
        }

        return PowerShellWasmCommandUtilities.EnumerateInput([scope])
            .Select(PowerShellWasmCommandUtilities.ToInvariantString)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    public static string GetUserImpersonationScope(string resourceUrl) =>
        resourceUrl.TrimEnd('/') + "/user_impersonation";

    public static Dictionary<string, object?> CloneRecord(IReadOnlyDictionary<string, object?> record) =>
        new(record, StringComparer.OrdinalIgnoreCase);

    public static void ThrowIfUnsupportedParameters(PowerShellWasmCommandContext context, params string[] parameterNames)
    {
        foreach (var parameterName in parameterNames)
        {
            if (context.Parameters.ContainsKey(parameterName))
            {
                throw new InvalidOperationException(
                    $"{parameterName} is not supported by browser-safe Azure authentication.");
            }
        }
    }
}
