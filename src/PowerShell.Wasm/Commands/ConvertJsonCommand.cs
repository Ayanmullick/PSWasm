using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PSWasm.Commands;

internal sealed class ConvertToJsonCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var input = GetInput(context);
        var compress = context.Parameters.TryGetValue("Compress", out var compressValue) &&
            PowerShellWasmCommandUtilities.ToBoolean(compressValue);
        var depth = Math.Max(1, context.GetInt32("Depth", 8));

        context.ExecutionContext.WriteOutput(PowerShellWasmJson.ConvertToJson(input, compress, depth));
        return ValueTask.CompletedTask;
    }

    private static object? GetInput(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            return inputObject;
        }

        var input = context.PipelineInput.Count > 0
            ? PowerShellWasmCommandUtilities.EnumerateInput(context.PipelineInput).ToArray()
            : context.Arguments.ToArray();

        return input.Length switch
        {
            0 => null,
            1 => input[0],
            _ => input
        };
    }
}

internal sealed class ConvertFromJsonCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        foreach (var input in GetInputs(context))
        {
            var json = Convert.ToString(input, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            context.ExecutionContext.WriteOutput(PowerShellWasmJson.ConvertFromJson(json));
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<object?> GetInputs(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("InputObject", out var inputObject))
        {
            yield return inputObject;
            yield break;
        }

        if (context.PipelineInput.Count > 0)
        {
            foreach (var input in context.PipelineInput)
            {
                yield return input;
            }

            yield break;
        }

        foreach (var argument in context.Arguments)
        {
            yield return argument;
        }
    }
}

internal static class PowerShellWasmJson
{
    public static string ConvertToJson(object? value, bool compress, int depth)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = !compress });
        WriteJsonValue(writer, value, depth, currentDepth: 0);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static object? ConvertFromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadJsonValue(document.RootElement);
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value, int depth, int currentDepth)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (currentDepth >= depth)
        {
            writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteDictionary(writer, readOnlyDictionary, depth, currentDepth);
                return;
            case IDictionary<string, object?> dictionary:
                WriteDictionary(writer, dictionary, depth, currentDepth);
                return;
            case System.Collections.IDictionary legacyDictionary:
                WriteLegacyDictionary(writer, legacyDictionary, depth, currentDepth);
                return;
            case System.Collections.IEnumerable enumerable:
                WriteArray(writer, enumerable, depth, currentDepth);
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void WriteDictionary(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>> dictionary,
        int depth,
        int currentDepth)
    {
        writer.WriteStartObject();
        foreach (var item in dictionary)
        {
            writer.WritePropertyName(item.Key);
            WriteJsonValue(writer, item.Value, depth, currentDepth + 1);
        }

        writer.WriteEndObject();
    }

    private static void WriteLegacyDictionary(
        Utf8JsonWriter writer,
        System.Collections.IDictionary dictionary,
        int depth,
        int currentDepth)
    {
        writer.WriteStartObject();
        foreach (System.Collections.DictionaryEntry item in dictionary)
        {
            writer.WritePropertyName(Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? string.Empty);
            WriteJsonValue(writer, item.Value, depth, currentDepth + 1);
        }

        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, System.Collections.IEnumerable array, int depth, int currentDepth)
    {
        writer.WriteStartArray();
        foreach (var item in array)
        {
            WriteJsonValue(writer, item, depth, currentDepth + 1);
        }

        writer.WriteEndArray();
    }

    private static object? ReadJsonValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(static item => item.Name, static item => ReadJsonValue(item.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ReadJsonValue).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ReadJsonNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };

    private static object ReadJsonNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (element.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        return element.GetDouble();
    }
}
