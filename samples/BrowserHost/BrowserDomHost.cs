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

    public ValueTask RegisterEventAsync(PowerShellWasmDomEventRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        s_eventRegistrations[registration.Id] = registration;
        RegisterEvent(registration.Id, registration.Selector, registration.Event, registration.PreventDefault);
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

    [JSImport("globalThis.pswasmDom.getText")]
    private static partial string GetText(string selector);

    [JSImport("globalThis.pswasmDom.setText")]
    private static partial void SetText(string selector, string text);

    [JSImport("globalThis.pswasmDom.getValue")]
    private static partial string GetValue(string selector);

    [JSImport("globalThis.pswasmDom.setProperty")]
    private static partial void SetProperty(string selector, string propertyName, string valueJson);

    [JSImport("globalThis.pswasmDom.registerEvent")]
    private static partial void RegisterEvent(int registrationId, string selector, string eventName, bool preventDefault);
}
