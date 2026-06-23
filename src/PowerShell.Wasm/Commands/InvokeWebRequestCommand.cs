using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PSWasm.Commands;

internal sealed class InvokeWebRequestCommand(HttpClient httpClient) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        using var response = await WebRequestCommandUtilities.SendAsync(
            httpClient,
            context,
            "Invoke-WebRequest",
            cancellationToken);
        context.ExecutionContext.WriteOutput(WebRequestCommandUtilities.CreateResponseObject(response.Message, response.Content));
    }
}

internal sealed class InvokeRestMethodCommand(HttpClient httpClient) : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        using var response = await WebRequestCommandUtilities.SendAsync(
            httpClient,
            context,
            "Invoke-RestMethod",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return;
        }

        context.ExecutionContext.WriteOutput(WebRequestCommandUtilities.ConvertRestContent(response.Message, response.Content));
    }
}

internal sealed class WebRequestCommandResponse(HttpResponseMessage message, string content) : IDisposable
{
    public HttpResponseMessage Message { get; } = message;

    public string Content { get; } = content;

    public void Dispose() => Message.Dispose();
}

internal static class WebRequestCommandUtilities
{
    public static async Task<WebRequestCommandResponse> SendAsync(
        HttpClient httpClient,
        PowerShellWasmCommandContext context,
        string commandName,
        CancellationToken cancellationToken)
    {
        var uri = GetUri(context, commandName);
        using var request = new HttpRequestMessage(GetMethod(context), uri);

        AddHeaders(request, context);
        AddBody(request, context);

        var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var skipHttpErrorCheck = context.Parameters.TryGetValue("SkipHttpErrorCheck", out var skipValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(skipValue);

        if (!skipHttpErrorCheck && !response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"{commandName} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return new(response, content);
    }

    public static Dictionary<string, object?> CreateResponseObject(HttpResponseMessage response, string content)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(static item => item.Key, static item => FormatHeaderValue(item.Value), StringComparer.OrdinalIgnoreCase);
        var rawContent = BuildRawContent(response, headers, content);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["StatusCode"] = (int)response.StatusCode,
            ["StatusDescription"] = response.ReasonPhrase ?? response.StatusCode.ToString(),
            ["Headers"] = headers,
            ["Content"] = content,
            ["RawContent"] = rawContent,
            ["RawContentLength"] = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(content)
        };
    }

    public static object? ConvertRestContent(HttpResponseMessage response, string content)
    {
        if (!ContentTypeIsJson(response.Content.Headers.ContentType?.MediaType))
        {
            return content;
        }

        try
        {
            return PowerShellWasmJson.ConvertFromJson(content);
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"Invoke-RestMethod failed to parse JSON response: {error.Message}", error);
        }
    }

    private static Uri GetUri(PowerShellWasmCommandContext context, string commandName)
    {
        var text = context.GetString("Uri") ??
            context.GetString("Url") ??
            context.Arguments.Select(PowerShellWasmCommandUtilities.ToInvariantString)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{commandName} requires a Uri.");
        }

        return new Uri(text, UriKind.RelativeOrAbsolute);
    }

    private static HttpMethod GetMethod(PowerShellWasmCommandContext context)
    {
        var method = context.GetString("CustomMethod") ?? context.GetString("Method") ?? "GET";
        return new HttpMethod(method.ToUpperInvariant());
    }

    private static void AddHeaders(HttpRequestMessage request, PowerShellWasmCommandContext context)
    {
        if (!OperatingSystem.IsBrowser() && context.GetString("UserAgent") is { Length: > 0 } userAgent)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (!context.Parameters.TryGetValue("Headers", out var headers))
        {
            return;
        }

        foreach (var (name, value) in EnumerateDictionary(headers))
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, EnumerateHeaderValues(value));
        }
    }

    private static void AddBody(HttpRequestMessage request, PowerShellWasmCommandContext context)
    {
        if (!context.Parameters.TryGetValue("Body", out var body))
        {
            return;
        }

        var contentType = context.GetString("ContentType") ?? GetContentTypeHeader(context);
        request.Content = CreateContent(body, contentType);
    }

    private static HttpContent CreateContent(object? body, string? contentType)
    {
        HttpContent content;
        if (body is System.Collections.IDictionary or IReadOnlyDictionary<string, object?> &&
            !ContentTypeIsJson(contentType))
        {
            content = new FormUrlEncodedContent(EnumerateDictionary(body)
                .Select(static item => new KeyValuePair<string, string>(item.Key,
                    PowerShellWasmCommandUtilities.ToInvariantString(item.Value))));
        }
        else
        {
            var text = body is System.Collections.IDictionary or IReadOnlyDictionary<string, object?>
                ? PowerShellWasmJson.ConvertToJson(body, compress: true, depth: 8)
                : PowerShellWasmCommandUtilities.ToInvariantString(body);
            content = new StringContent(text, Encoding.UTF8);
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return content;
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

    private static string BuildRawContent(HttpResponseMessage response, IReadOnlyDictionary<string, object?> headers, string content)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/").Append(response.Version).Append(' ')
            .Append((int)response.StatusCode).Append(' ')
            .AppendLine(response.ReasonPhrase);

        foreach (var header in headers)
        {
            builder.Append(header.Key).Append(": ")
                .AppendLine(FormatHeaderForRawContent(header.Value));
        }

        builder.AppendLine();
        builder.Append(content);
        return builder.ToString();
    }

    private static object? FormatHeaderValue(IEnumerable<string> values)
    {
        var array = values.Cast<object?>().ToArray();
        return array.Length == 1 ? array[0] ?? string.Empty : array;
    }

    private static string FormatHeaderForRawContent(object? value) =>
        value is object?[] array
            ? string.Join(", ", array.Select(PowerShellWasmCommandUtilities.ToInvariantString))
            : PowerShellWasmCommandUtilities.ToInvariantString(value);

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

    private static bool ContentTypeIsJson(string? contentType) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}
