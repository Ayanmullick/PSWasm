using System.Globalization;
#if PSWASM_CRYPTO
using System.Security.Cryptography;
using System.Text;
#endif

namespace PSWasm.Language;

// Browser-safe .NET member bridge.
// dotnet/runtime source references:
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Convert.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Text/Encoding.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Text/UTF8Encoding.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/HMACSHA256.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.Uri/src/System/UriExt.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs
// Browser note: this delegates to platform .NET APIs behind an allowlist instead of exposing arbitrary reflection.
internal static class PowerShellWasmDotNetBridge
{
#if PSWASM_CRYPTO
    private const string ConvertType = "System.Convert";
    private const string EncodingType = "System.Text.Encoding";
    private const string HmacSha256Type = "System.Security.Cryptography.HMACSHA256";
    private const string UriType = "System.Uri";
#endif
    private const string StringType = "System.String";

    private static readonly IReadOnlyDictionary<string, string> TypeAliases = CreateTypeAliases();

    private static IReadOnlyDictionary<string, string> CreateTypeAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#if PSWASM_CRYPTO
        aliases["Convert"] = ConvertType;
        aliases["System.Convert"] = ConvertType;
        aliases["Text.Encoding"] = EncodingType;
        aliases["System.Text.Encoding"] = EncodingType;
        aliases["HMACSHA256"] = HmacSha256Type;
        aliases["Security.Cryptography.HMACSHA256"] = HmacSha256Type;
        aliases["System.Security.Cryptography.HMACSHA256"] = HmacSha256Type;
        aliases["Uri"] = UriType;
        aliases["System.Uri"] = UriType;
#endif
        return aliases;
    }

    internal static PowerShellWasmDotNetType ResolveType(string typeName)
    {
        if (TypeAliases.TryGetValue(typeName.Trim(), out var fullName))
        {
            return new PowerShellWasmDotNetType(fullName);
        }

        throw new InvalidOperationException($"Type literal [{typeName}] is not available in this browser-safe runtime.");
    }

    internal static bool TryGetStaticMember(object? target, string memberName, out object? value)
    {
        value = null;
        if (target is not PowerShellWasmDotNetType type)
        {
            return false;
        }

#if PSWASM_CRYPTO
        if (type.FullName.Equals(ConvertType, StringComparison.OrdinalIgnoreCase) &&
            IsMember(memberName, "FromBase64String", "ToBase64String"))
        {
            value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
            return true;
        }

        if (type.FullName.Equals(EncodingType, StringComparison.OrdinalIgnoreCase) &&
            memberName.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            value = Encoding.UTF8;
            return true;
        }

        if (type.FullName.Equals(HmacSha256Type, StringComparison.OrdinalIgnoreCase) &&
            memberName.Equals("HashData", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
            return true;
        }

        if (type.FullName.Equals(UriType, StringComparison.OrdinalIgnoreCase) &&
            IsMember(memberName, "EscapeDataString", "UnescapeDataString"))
        {
            value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
            return true;
        }
#endif

        return false;
    }

    internal static bool TryGetInstanceMember(object target, string memberName, out object? value)
    {
        value = null;
#if PSWASM_CRYPTO
        if (target is Encoding && memberName.Equals("GetBytes", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(EncodingType, memberName, target, IsStatic: false);
            return true;
        }
#endif

        if (target is string && IsMember(memberName, "ToLowerInvariant", "ToUpperInvariant", "ToString", "Trim", "Substring"))
        {
            value = new PowerShellWasmDotNetMethod(StringType, memberName, target, IsStatic: false);
            return true;
        }

        return false;
    }

    internal static bool TryInvoke(object? target, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;
        if (target is not PowerShellWasmDotNetMethod method)
        {
            return false;
        }

        value = Invoke(method, arguments);
        return true;
    }

    private static object Invoke(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
#if PSWASM_CRYPTO
        if (method.DeclaringType.Equals(ConvertType, StringComparison.OrdinalIgnoreCase))
        {
            if (method.Name.Equals("FromBase64String", StringComparison.OrdinalIgnoreCase))
            {
                RequireArgumentCount(method, arguments, 1);
                return Convert.FromBase64String(ToInvariantString(arguments[0]));
            }

            if (method.Name.Equals("ToBase64String", StringComparison.OrdinalIgnoreCase))
            {
                RequireArgumentCount(method, arguments, 1);
                return Convert.ToBase64String(ToByteArray(arguments[0]));
            }
        }

        if (method.DeclaringType.Equals(EncodingType, StringComparison.OrdinalIgnoreCase) &&
            method.Target is Encoding encoding &&
            method.Name.Equals("GetBytes", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 1);
            return encoding.GetBytes(ToInvariantString(arguments[0]));
        }

        if (method.DeclaringType.Equals(HmacSha256Type, StringComparison.OrdinalIgnoreCase) &&
            method.Name.Equals("HashData", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 2);
            return HMACSHA256.HashData(ToByteArray(arguments[0]), ToByteArray(arguments[1]));
        }

        if (method.DeclaringType.Equals(UriType, StringComparison.OrdinalIgnoreCase) &&
            method.Name.Equals("EscapeDataString", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 1);
            return Uri.EscapeDataString(ToInvariantString(arguments[0]));
        }

        if (method.DeclaringType.Equals(UriType, StringComparison.OrdinalIgnoreCase) &&
            method.Name.Equals("UnescapeDataString", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 1);
            return Uri.UnescapeDataString(ToInvariantString(arguments[0]));
        }
#endif

        if (method.DeclaringType.Equals(StringType, StringComparison.OrdinalIgnoreCase) &&
            method.Target is string text)
        {
            return method.Name.ToLowerInvariant() switch
            {
                "tolowerinvariant" => InvokeStringNoArgument(method, arguments, text.ToLowerInvariant),
                "toupperinvariant" => InvokeStringNoArgument(method, arguments, text.ToUpperInvariant),
                "tostring" => InvokeStringNoArgument(method, arguments, () => text),
                "trim" => InvokeStringNoArgument(method, arguments, text.Trim),
                "substring" => InvokeSubstring(method, arguments, text),
                _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
            };
        }

        throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.");
    }

    private static bool IsMember(string memberName, params string[] supportedMembers) =>
        supportedMembers.Any(supported => supported.Equals(memberName, StringComparison.OrdinalIgnoreCase));

    private static string InvokeStringNoArgument(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<string> invoke)
    {
        RequireArgumentCount(method, arguments, 0);
        return invoke();
    }

    private static string InvokeSubstring(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments, string text)
    {
        if (arguments.Count == 1)
        {
            return text.Substring(ToInt32(arguments[0]));
        }

        if (arguments.Count == 2)
        {
            return text.Substring(ToInt32(arguments[0]), ToInt32(arguments[1]));
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 1 or 2 argument(s), but received {arguments.Count}.");
    }

    private static void RequireArgumentCount(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments, int count)
    {
        if (arguments.Count != count)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' expects {count} argument(s), but received {arguments.Count}.");
        }
    }

    private static byte[] ToByteArray(object? value)
    {
        if (value is byte[] bytes)
        {
            return bytes;
        }

        if (value is null)
        {
            return [];
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var result = new List<byte>();
            foreach (var item in enumerable)
            {
                result.Add(Convert.ToByte(item, CultureInfo.InvariantCulture));
            }

            return result.ToArray();
        }

        throw new InvalidOperationException($"Value '{value}' cannot be converted to a byte array.");
    }

    private static string ToInvariantString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static int ToInt32(object? value) =>
        Convert.ToInt32(value, CultureInfo.InvariantCulture);
}

internal sealed record PowerShellWasmDotNetType(string FullName)
{
    public override string ToString() =>
        $"[{FullName}]";
}

internal sealed record PowerShellWasmDotNetMethod(
    string DeclaringType,
    string Name,
    object? Target,
    bool IsStatic);
