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
internal sealed partial class BrowserHostJsonContext : JsonSerializerContext;
