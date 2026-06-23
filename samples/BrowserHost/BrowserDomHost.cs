using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PSWasm;

namespace PSWasm.BrowserHost;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserDomHost : IPowerShellWasmDomHost
{
    private static readonly Dictionary<int, PowerShellWasmDomEventRegistration> s_eventRegistrations = [];

    public ValueTask<string> GetTextAsync(string selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetText(selector));
    }

    public ValueTask SetTextAsync(string selector, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetText(selector, text);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetHtmlAsync(string selector, string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetHtml(selector, html);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> GetValueAsync(string selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetValue(selector));
    }

    public ValueTask SetPropertyAsync(string selector, string propertyName, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetProperty(selector, propertyName, SerializeDomValue(value));
        return ValueTask.CompletedTask;
    }

    public ValueTask<object?> GetPropertyAsync(string selector, string propertyName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ParseJsonValue(GetProperty(selector, propertyName)));
    }

    public ValueTask RegisterEventAsync(PowerShellWasmDomEventRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        s_eventRegistrations[registration.Id] = registration;
        RegisterEvent(registration.Id, registration.Selector, registration.Event, registration.PreventDefault);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterEventAsync(int registrationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        s_eventRegistrations.Remove(registrationId);
        UnregisterEvent(registrationId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetStorageItemAsync(string storage, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ParseStorageItem(GetStorageItem(storage, key)));
    }

    public ValueTask SetStorageItemAsync(string storage, string key, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetStorageItem(storage, key, value);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveStorageItemAsync(string storage, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveStorageItem(storage, key);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearStorageAsync(string storage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearStorage(storage);
        return ValueTask.CompletedTask;
    }

    public ValueTask RegisterStorageBindingAsync(PowerShellWasmDomStorageBindingRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisterStorageBinding(registration.Id, registration.Storage, SerializeMap(registration.Map), registration.Event, registration.Property);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterStorageBindingAsync(int registrationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnregisterStorageBinding(registrationId);
        return ValueTask.CompletedTask;
    }

    public static async Task<string> InvokeEventJsonAsync(int registrationId, string eventJson)
    {
        if (!s_eventRegistrations.TryGetValue(registrationId, out var registration))
        {
            throw new InvalidOperationException($"DOM event registration '{registrationId}' does not exist.");
        }

        var eventData = ParseEventData(eventJson);
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventData"] = eventData
        };
        var result = await registration.ScriptBlock.InvokeResultAsync(eventData, variables);
        return JsonSerializer.Serialize(
            new BrowserPowerShellResult(result.Text, result.Records),
            BrowserHostJsonContext.Default.BrowserPowerShellResult);
    }

    private static Dictionary<string, object?> ParseEventData(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(json);
        return ReadElement(document.RootElement) as Dictionary<string, object?> ??
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ReadElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ReadObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ReadElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    private static Dictionary<string, object?> ReadObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ReadElement(property.Value);
        }

        return result;
    }

    private static string SerializeDomValue(object? value) =>
        value switch
        {
            null => "null",
            bool boolValue => boolValue ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            _ => ToJsonString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };

    private static string ToJsonString(string value) =>
        "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    private static string SerializeMap(IReadOnlyDictionary<string, string> map) =>
        "{" + string.Join(",", map.Select(static item => $"{ToJsonString(item.Key)}:{ToJsonString(item.Value)}")) + "}";

    private static string? ParseStorageItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("exists", out var exists) &&
            exists.ValueKind == JsonValueKind.True
                ? document.RootElement.GetProperty("value").GetString() ?? string.Empty
                : null;
    }

    private static object? ParseJsonValue(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ReadElement(document.RootElement);
    }

    [JSImport("globalThis.pswasmDom.getText")]
    private static partial string GetText(string selector);

    [JSImport("globalThis.pswasmDom.setText")]
    private static partial void SetText(string selector, string text);

    [JSImport("globalThis.pswasmDom.setHtml")]
    private static partial void SetHtml(string selector, string html);

    [JSImport("globalThis.pswasmDom.getValue")]
    private static partial string GetValue(string selector);

    [JSImport("globalThis.pswasmDom.setProperty")]
    private static partial void SetProperty(string selector, string propertyName, string valueJson);

    [JSImport("globalThis.pswasmDom.getProperty")]
    private static partial string GetProperty(string selector, string propertyName);

    [JSImport("globalThis.pswasmDom.registerEvent")]
    private static partial void RegisterEvent(int registrationId, string selector, string eventName, bool preventDefault);

    [JSImport("globalThis.pswasmDom.unregisterEvent")]
    private static partial void UnregisterEvent(int registrationId);

    [JSImport("globalThis.pswasmDom.getStorageItem")]
    private static partial string GetStorageItem(string storage, string key);

    [JSImport("globalThis.pswasmDom.setStorageItem")]
    private static partial void SetStorageItem(string storage, string key, string value);

    [JSImport("globalThis.pswasmDom.removeStorageItem")]
    private static partial void RemoveStorageItem(string storage, string key);

    [JSImport("globalThis.pswasmDom.clearStorage")]
    private static partial void ClearStorage(string storage);

    [JSImport("globalThis.pswasmDom.registerStorageBinding")]
    private static partial void RegisterStorageBinding(int registrationId, string storage, string mapJson, string eventName, string propertyName);

    [JSImport("globalThis.pswasmDom.unregisterStorageBinding")]
    private static partial void UnregisterStorageBinding(int registrationId);
}
