using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace PSWasm.Commands;

// Browser-safe subset modeled after Az.Accounts Invoke-AzRestMethod:
// - https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/Rest/InvokeAzRestMethodCommand.cs
// - https://github.com/Azure/azure-powershell/blob/main/src/Accounts/Accounts/help/Invoke-AzRestMethod.md
// The desktop command builds authenticated Azure REST clients from Az.Accounts context.
// PSWasm keeps the same PowerShell-facing shape where practical, then uses the browser
// auth host and HttpClient instead of desktop Az profile/client factories.
internal sealed class InvokeAzRestMethodCommand(
    HttpClient httpClient,
    IPowerShellWasmAzureAuthHost? authHost) : IPowerShellWasmCommand
{
    private const string ArmBaseUri = "https://management.azure.com";
    private const string ArmResourceUrl = "https://management.azure.com/";
    private const string CosmosResourceUrl = "https://cosmos.azure.com/";

    private static readonly HashSet<string> SupportedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "POST",
        "PUT",
        "PATCH",
        "DELETE"
    };

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        AzureAuthCommandUtilities.ThrowIfUnsupportedParameters(
            context,
            "SubscriptionId",
            "ResourceGroupName",
            "ResourceProviderName",
            "ResourceType",
            "Name",
            "ApiVersion",
            "AsJob",
            "WaitForCompletion",
            "PollFrom",
            "FinalResultFrom",
            "Paginate",
            "NextLinkName",
            "PageableItemName",
            "MaxPageSize",
            "DefaultProfile",
            "WhatIf",
            "Confirm");

        var requestTarget = ResolveRequestTarget(context);
        using var request = new HttpRequestMessage(GetMethod(context), requestTarget.Uri);

        AddUserHeaders(request, context);
        await AddAuthorizationHeaderAsync(request, context, requestTarget, cancellationToken);
        AddPayload(request, context);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        context.ExecutionContext.WriteOutput(CreateResponseObject(request, response, content));
    }

    private static AzureRestRequestTarget ResolveRequestTarget(PowerShellWasmCommandContext context)
    {
        var path = context.GetString("Path");
        var uriText = context.GetString("Uri") ?? GetFirstArgumentText(context);
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(uriText))
        {
            throw new InvalidOperationException("Invoke-AzRestMethod accepts either -Path or -Uri, not both.");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var normalizedPath = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
            return new(new Uri(ArmBaseUri + normalizedPath, UriKind.Absolute), ArmResourceUrl, AzureRestAuthorizationStyle.Bearer);
        }

        if (string.IsNullOrWhiteSpace(uriText))
        {
            throw new InvalidOperationException("Invoke-AzRestMethod requires -Path or -Uri.");
        }

        var uri = new Uri(uriText, UriKind.Absolute);
        var resourceUrl = context.GetString("ResourceId") ?? InferResourceUrl(uri);
        var style = IsCosmosDocumentsHost(uri) ? AzureRestAuthorizationStyle.CosmosAad : AzureRestAuthorizationStyle.Bearer;
        return new(uri, resourceUrl, style);
    }

    private static string? GetFirstArgumentText(PowerShellWasmCommandContext context)
    {
        foreach (var argument in context.Arguments)
        {
            var text = PowerShellWasmCommandUtilities.ToInvariantString(argument);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static HttpMethod GetMethod(PowerShellWasmCommandContext context)
    {
        var method = (context.GetString("Method") ?? "GET").ToUpperInvariant();
        if (!SupportedMethods.Contains(method))
        {
            throw new InvalidOperationException("Invoke-AzRestMethod supports GET, POST, PUT, PATCH, and DELETE.");
        }

        return new HttpMethod(method);
    }

    private static string InferResourceUrl(Uri uri)
    {
        if (IsCosmosDocumentsHost(uri))
        {
            return CosmosResourceUrl;
        }

        return uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)
            ? ArmResourceUrl
            : $"{uri.Scheme}://{uri.Authority}/";
    }

    private static bool IsCosmosDocumentsHost(Uri uri) =>
        uri.Host.EndsWith(".documents.azure.com", StringComparison.OrdinalIgnoreCase);

    private static void AddUserHeaders(HttpRequestMessage request, PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Headers", out var headers))
        {
            return;
        }

        foreach (var (name, value) in EnumerateDictionary(headers))
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Invoke-AzRestMethod manages the Authorization header.");
            }

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, EnumerateHeaderValues(value));
        }
    }

    private async ValueTask AddAuthorizationHeaderAsync(
        HttpRequestMessage request,
        PowerShellWasmCommandContext context,
        AzureRestRequestTarget target,
        CancellationToken cancellationToken)
    {
        var host = AzureAuthCommandUtilities.GetAuthHost(authHost);
        var token = await host.GetAccessTokenAsync(
            new PowerShellWasmAzureAuthTokenRequest(
                target.ResourceUrl,
                [AzureAuthCommandUtilities.GetUserImpersonationScope(target.ResourceUrl)]),
            cancellationToken);
        var tokenText = PowerShellWasmCommandUtilities.ToInvariantString(
            PowerShellWasmCommandUtilities.GetMemberValue(token, "Token"));

        if (string.IsNullOrWhiteSpace(tokenText))
        {
            throw new InvalidOperationException("Browser Azure authentication did not return an access token.");
        }

        if (target.AuthorizationStyle == AzureRestAuthorizationStyle.CosmosAad)
        {
            var authorization = Uri.EscapeDataString($"type=aad&ver=1.0&sig={tokenText}");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenText);
    }

    private static void AddPayload(HttpRequestMessage request, PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Payload", out var payload))
        {
            return;
        }

        var text = PowerShellWasmCommandUtilities.ToInvariantString(payload);
        var contentType = context.GetString("ContentType") ?? GetContentTypeHeader(context) ?? "application/json";
        request.Content = new StringContent(text, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
    }

    private static string? GetContentTypeHeader(PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Headers", out var headers))
        {
            return null;
        }

        foreach (var (name, value) in EnumerateDictionary(headers))
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                return PowerShellWasmCommandUtilities.ToInvariantString(value);
            }
        }

        return null;
    }

    private static Dictionary<string, object?> CreateResponseObject(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string content)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(static item => item.Key, static item => FormatHeaderValue(item.Value), StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Headers"] = headers,
            ["Version"] = response.Version.ToString(),
            ["StatusCode"] = (int)response.StatusCode,
            ["StatusDescription"] = response.ReasonPhrase ?? response.StatusCode.ToString(),
            ["Method"] = request.Method.Method,
            ["RequestUri"] = request.RequestUri?.ToString() ?? string.Empty,
            ["Content"] = content,
            ["RawContentLength"] = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(content)
        };
    }

    private static object? FormatHeaderValue(IEnumerable<string> values)
    {
        var array = values.Cast<object?>().ToArray();
        return array.Length == 1 ? array[0] ?? string.Empty : array;
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateDictionary(object? value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                foreach (var item in readOnlyDictionary)
                {
                    yield return item;
                }

                yield break;
            case IDictionary<string, object?> dictionary:
                foreach (var item in dictionary)
                {
                    yield return item;
                }

                yield break;
            case System.Collections.IDictionary legacyDictionary:
                foreach (System.Collections.DictionaryEntry item in legacyDictionary)
                {
                    yield return new(
                        Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? string.Empty,
                        item.Value);
                }

                yield break;
            default:
                yield break;
        }
    }

    private static IEnumerable<string> EnumerateHeaderValues(object? value)
    {
        if (value is string or null)
        {
            yield return PowerShellWasmCommandUtilities.ToInvariantString(value);
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return PowerShellWasmCommandUtilities.ToInvariantString(item);
            }

            yield break;
        }

        yield return PowerShellWasmCommandUtilities.ToInvariantString(value);
    }

    private sealed record AzureRestRequestTarget(
        Uri Uri,
        string ResourceUrl,
        AzureRestAuthorizationStyle AuthorizationStyle);

    private enum AzureRestAuthorizationStyle
    {
        Bearer,
        CosmosAad
    }
}
