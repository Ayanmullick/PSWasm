using System.Collections;
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
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Math.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/DateTime.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Collections/ArrayList.cs
// - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/List.cs
// PowerShell source references:
// - https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/LanguagePrimitives.cs
// - https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/parser/Parser.cs
// Browser note: this delegates to platform .NET APIs behind an allowlist instead of exposing arbitrary reflection.
internal static class PowerShellWasmDotNetBridge
{
    private const string MathType = "System.Math";
    private const string DateTimeType = "System.DateTime";
    private const string DateTimeOffsetType = "System.DateTimeOffset";
    private const string TimeSpanType = "System.TimeSpan";
    private const string TimeZoneInfoType = "System.TimeZoneInfo";
    private const string ArrayListType = "System.Collections.ArrayList";
    private const string GenericListTypePrefix = "System.Collections.Generic.List";
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
        aliases["Math"] = MathType;
        aliases["System.Math"] = MathType;
        aliases["DateTime"] = DateTimeType;
        aliases["datetime"] = DateTimeType;
        aliases["System.DateTime"] = DateTimeType;
        aliases["DateTimeOffset"] = DateTimeOffsetType;
        aliases["datetimeoffset"] = DateTimeOffsetType;
        aliases["System.DateTimeOffset"] = DateTimeOffsetType;
        aliases["TimeSpan"] = TimeSpanType;
        aliases["timespan"] = TimeSpanType;
        aliases["System.TimeSpan"] = TimeSpanType;
        aliases["TimeZoneInfo"] = TimeZoneInfoType;
        aliases["System.TimeZoneInfo"] = TimeZoneInfoType;
        aliases["ArrayList"] = ArrayListType;
        aliases["System.Collections.ArrayList"] = ArrayListType;
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
        if (TryResolveTypeName(typeName, out var fullName))
        {
            return new PowerShellWasmDotNetType(fullName);
        }

        throw new InvalidOperationException($"Type literal [{typeName}] is not available in this browser-safe runtime.");
    }

    internal static bool TryConstruct(string typeName, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;
        if (!TryResolveTypeName(typeName, out var fullName))
        {
            return false;
        }

        if (fullName.Equals(ArrayListType, StringComparison.OrdinalIgnoreCase))
        {
            value = ConstructArrayList(arguments);
            return true;
        }

        if (IsGenericListType(fullName))
        {
            value = ConstructGenericList(fullName, arguments);
            return true;
        }

        if (fullName.Equals(DateTimeType, StringComparison.OrdinalIgnoreCase))
        {
            value = ConstructDateTime(arguments);
            return true;
        }

        if (fullName.Equals(DateTimeOffsetType, StringComparison.OrdinalIgnoreCase))
        {
            value = ConstructDateTimeOffset(arguments);
            return true;
        }

        if (fullName.Equals(TimeSpanType, StringComparison.OrdinalIgnoreCase))
        {
            value = ConstructTimeSpan(arguments);
            return true;
        }

        throw new InvalidOperationException($"Type [{fullName}] is not available for New-Object in this browser-safe runtime.");
    }

    internal static bool TryCast(string typeName, object? input, out object? value)
    {
        value = null;
        if (!TryResolveTypeName(typeName, out var fullName))
        {
            return false;
        }

        if (fullName.Equals(DateTimeType, StringComparison.OrdinalIgnoreCase))
        {
            value = ToDateTime(input);
            return true;
        }

        if (fullName.Equals(DateTimeOffsetType, StringComparison.OrdinalIgnoreCase))
        {
            value = ToDateTimeOffset(input);
            return true;
        }

        if (fullName.Equals(TimeSpanType, StringComparison.OrdinalIgnoreCase))
        {
            value = ToTimeSpan(input);
            return true;
        }

        if (fullName.Equals(ArrayListType, StringComparison.OrdinalIgnoreCase))
        {
            value = new ArrayList(EnumerateForCollection(input).ToArray());
            return true;
        }

        if (IsGenericListType(fullName))
        {
            value = ConstructGenericList(fullName, EnumerateForCollection(input).ToArray());
            return true;
        }

        return false;
    }

    internal static bool TryTypeMatches(string typeName, object? input, out bool matches)
    {
        matches = false;
        if (!TryResolveTypeName(typeName, out var fullName))
        {
            return false;
        }

        matches = fullName switch
        {
            MathType => input is PowerShellWasmDotNetType type &&
                type.FullName.Equals(MathType, StringComparison.OrdinalIgnoreCase),
            DateTimeType => input is DateTime,
            DateTimeOffsetType => input is DateTimeOffset,
            TimeSpanType => input is TimeSpan,
            TimeZoneInfoType => input is TimeZoneInfo,
            ArrayListType => input is ArrayList,
            _ when IsGenericListType(fullName) => GenericListTypeMatches(fullName, input),
            _ => false
        };
        return true;
    }

    internal static bool TryGetStaticMember(object? target, string memberName, out object? value)
    {
        value = null;
        if (target is not PowerShellWasmDotNetType type)
        {
            return false;
        }

        if (type.FullName.Equals(MathType, StringComparison.OrdinalIgnoreCase))
        {
            if (memberName.Equals("PI", StringComparison.OrdinalIgnoreCase))
            {
                value = Math.PI;
                return true;
            }

            if (memberName.Equals("E", StringComparison.OrdinalIgnoreCase))
            {
                value = Math.E;
                return true;
            }

            if (IsMember(memberName, "Abs", "Ceiling", "Floor", "Round", "Truncate", "Sqrt", "Pow", "Max", "Min"))
            {
                value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
                return true;
            }
        }

        if (type.FullName.Equals(DateTimeType, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetDateTimeStaticProperty(memberName, out value))
            {
                return true;
            }

            if (IsMember(memberName, "Parse", "SpecifyKind", "IsLeapYear", "DaysInMonth"))
            {
                value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
                return true;
            }
        }

        if (type.FullName.Equals(DateTimeOffsetType, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetDateTimeOffsetStaticProperty(memberName, out value))
            {
                return true;
            }

            if (IsMember(memberName, "Parse"))
            {
                value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
                return true;
            }
        }

        if (type.FullName.Equals(TimeSpanType, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetTimeSpanStaticProperty(memberName, out value))
            {
                return true;
            }

            if (IsMember(memberName, "Parse", "FromDays", "FromHours", "FromMinutes", "FromSeconds", "FromMilliseconds"))
            {
                value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
                return true;
            }
        }

        if (type.FullName.Equals(TimeZoneInfoType, StringComparison.OrdinalIgnoreCase))
        {
            if (memberName.Equals("Utc", StringComparison.OrdinalIgnoreCase))
            {
                value = TimeZoneInfo.Utc;
                return true;
            }

            if (memberName.Equals("Local", StringComparison.OrdinalIgnoreCase))
            {
                value = TimeZoneInfo.Local;
                return true;
            }

            if (IsMember(
                memberName,
                "FindSystemTimeZoneById",
                "ConvertTime",
                "ConvertTimeToUtc",
                "ConvertTimeFromUtc",
                "ConvertTimeBySystemTimeZoneId"))
            {
                value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
                return true;
            }
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

        if (target is DateTime dateTime)
        {
            return TryGetDateTimeInstanceMember(dateTime, memberName, out value);
        }

        if (target is DateTimeOffset dateTimeOffset)
        {
            return TryGetDateTimeOffsetInstanceMember(dateTimeOffset, memberName, out value);
        }

        if (target is TimeSpan timeSpan)
        {
            return TryGetTimeSpanInstanceMember(timeSpan, memberName, out value);
        }

        if (target is TimeZoneInfo timeZone)
        {
            return TryGetTimeZoneInfoInstanceMember(timeZone, memberName, out value);
        }

        if (target is IList list && TryGetListInstanceMember(list, memberName, out value))
        {
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

    private static object? Invoke(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
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

        if (method.DeclaringType.Equals(MathType, StringComparison.OrdinalIgnoreCase))
        {
            return InvokeMath(method, arguments);
        }

        if (method.DeclaringType.Equals(DateTimeType, StringComparison.OrdinalIgnoreCase))
        {
            return InvokeDateTime(method, arguments);
        }

        if (method.DeclaringType.Equals(DateTimeOffsetType, StringComparison.OrdinalIgnoreCase))
        {
            return InvokeDateTimeOffset(method, arguments);
        }

        if (method.DeclaringType.Equals(TimeSpanType, StringComparison.OrdinalIgnoreCase))
        {
            return InvokeTimeSpan(method, arguments);
        }

        if (method.DeclaringType.Equals(TimeZoneInfoType, StringComparison.OrdinalIgnoreCase))
        {
            return InvokeTimeZoneInfo(method, arguments);
        }

        if ((method.DeclaringType.Equals(ArrayListType, StringComparison.OrdinalIgnoreCase) ||
            method.DeclaringType.Equals(GenericListTypePrefix, StringComparison.OrdinalIgnoreCase)) &&
            method.Target is IList list)
        {
            return InvokeList(method, arguments, list);
        }

        throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.");
    }

    private static bool TryResolveTypeName(string typeName, out string fullName)
    {
        fullName = string.Empty;
        var trimmed = typeName.Trim();
        if (TryNormalizeGenericListType(trimmed, out fullName))
        {
            return true;
        }

        return TypeAliases.TryGetValue(trimmed, out fullName!);
    }

    private static bool TryNormalizeGenericListType(string typeName, out string fullName)
    {
        fullName = string.Empty;
        var compact = RemoveTypeNameWhitespace(typeName);
        foreach (var prefix in new[] { GenericListTypePrefix, "Collections.Generic.List", "List" })
        {
            if (!compact.StartsWith(prefix + "[", StringComparison.OrdinalIgnoreCase) ||
                !compact.EndsWith("]", StringComparison.Ordinal))
            {
                continue;
            }

            var elementType = compact[(prefix.Length + 1)..^1];
            if (!TryNormalizeListElementType(elementType, out var normalizedElementType))
            {
                throw new InvalidOperationException(
                    $"Generic list element type '[{elementType}]' is not available in this browser-safe runtime.");
            }

            fullName = $"{GenericListTypePrefix}[{normalizedElementType}]";
            return true;
        }

        return false;
    }

    private static string RemoveTypeNameWhitespace(string value) =>
        new(value.Where(static ch => !char.IsWhiteSpace(ch)).ToArray());

    private static bool TryNormalizeListElementType(string typeName, out string normalized)
    {
        normalized = typeName switch
        {
            var value when IsTypeName(value, "string", "System.String") => "string",
            var value when IsTypeName(value, "object", "System.Object") => "object",
            var value when IsTypeName(value, "int", "int32", "System.Int32") => "int",
            var value when IsTypeName(value, "long", "int64", "System.Int64") => "long",
            var value when IsTypeName(value, "double", "System.Double") => "double",
            var value when IsTypeName(value, "decimal", "System.Decimal") => "decimal",
            var value when IsTypeName(value, "bool", "boolean", "System.Boolean") => "bool",
            var value when IsTypeName(value, "byte", "System.Byte") => "byte",
            var value when IsTypeName(value, "datetime", "DateTime", DateTimeType) => "datetime",
            _ => string.Empty
        };

        return normalized.Length > 0;
    }

    private static bool IsTypeName(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsGenericListType(string typeName) =>
        typeName.StartsWith(GenericListTypePrefix + "[", StringComparison.OrdinalIgnoreCase) &&
        typeName.EndsWith("]", StringComparison.Ordinal);

    private static string GetGenericListElementType(string typeName) =>
        typeName[(GenericListTypePrefix.Length + 1)..^1];

    private static bool GenericListTypeMatches(string typeName, object? input) =>
        GetGenericListElementType(typeName) switch
        {
            "string" => input is List<string>,
            "object" => input is List<object?>,
            "int" => input is List<int>,
            "long" => input is List<long>,
            "double" => input is List<double>,
            "decimal" => input is List<decimal>,
            "bool" => input is List<bool>,
            "byte" => input is List<byte>,
            "datetime" => input is List<DateTime>,
            _ => false
        };

    private static ArrayList ConstructArrayList(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return [];
        }

        if (arguments.Count == 1)
        {
            if (arguments[0] is null)
            {
                return [];
            }

            if (arguments[0] is int capacity)
            {
                return new ArrayList(capacity);
            }

            if (arguments[0] is ICollection collection)
            {
                return new ArrayList(collection);
            }
        }

        return new ArrayList(arguments.ToArray());
    }

    private static object ConstructGenericList(string typeName, IReadOnlyList<object?> arguments)
    {
        var values = arguments.Count == 1 && arguments[0] is not null and IEnumerable and not string
            ? EnumerateForCollection(arguments[0]).ToArray()
            : arguments.ToArray();

        return GetGenericListElementType(typeName) switch
        {
            "string" => values.Select(ToInvariantString).ToList(),
            "object" => values.ToList(),
            "int" => values.Select(ToInt32).ToList(),
            "long" => values.Select(ToInt64).ToList(),
            "double" => values.Select(ToDouble).ToList(),
            "decimal" => values.Select(ToDecimal).ToList(),
            "bool" => values.Select(ToBoolean).ToList(),
            "byte" => values.Select(item => Convert.ToByte(item, CultureInfo.InvariantCulture)).ToList(),
            "datetime" => values.Select(ToDateTime).ToList(),
            _ => throw new InvalidOperationException($"Generic list type [{typeName}] is not available in this browser-safe runtime.")
        };
    }

    private static object ConstructDateTime(IReadOnlyList<object?> arguments) =>
        arguments.Count switch
        {
            0 => default(DateTime),
            1 => new DateTime(ToInt64(arguments[0]), DateTimeKind.Unspecified),
            3 => new DateTime(ToInt32(arguments[0]), ToInt32(arguments[1]), ToInt32(arguments[2])),
            6 => new DateTime(
                ToInt32(arguments[0]),
                ToInt32(arguments[1]),
                ToInt32(arguments[2]),
                ToInt32(arguments[3]),
                ToInt32(arguments[4]),
                ToInt32(arguments[5])),
            _ => throw new InvalidOperationException("New-Object DateTime supports 0, 1, 3, or 6 constructor argument(s).")
        };

    private static object ConstructDateTimeOffset(IReadOnlyList<object?> arguments) =>
        arguments.Count switch
        {
            0 => default(DateTimeOffset),
            1 => new DateTimeOffset(ToDateTime(arguments[0])),
            _ => throw new InvalidOperationException("New-Object DateTimeOffset supports 0 or 1 constructor argument(s).")
        };

    private static object ConstructTimeSpan(IReadOnlyList<object?> arguments) =>
        arguments.Count switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromTicks(ToInt64(arguments[0])),
            3 => new TimeSpan(ToInt32(arguments[0]), ToInt32(arguments[1]), ToInt32(arguments[2])),
            4 => new TimeSpan(ToInt32(arguments[0]), ToInt32(arguments[1]), ToInt32(arguments[2]), ToInt32(arguments[3])),
            _ => throw new InvalidOperationException("New-Object TimeSpan supports 0, 1, 3, or 4 constructor argument(s).")
        };

    private static IEnumerable<object?> EnumerateForCollection(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string or byte[])
        {
            yield return value;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private static bool TryGetDateTimeStaticProperty(string memberName, out object? value)
    {
        value = memberName.ToLowerInvariant() switch
        {
            "now" => DateTime.Now,
            "utcnow" => DateTime.UtcNow,
            "today" => DateTime.Today,
            "minvalue" => DateTime.MinValue,
            "maxvalue" => DateTime.MaxValue,
            _ => null
        };

        return value is not null;
    }

    private static bool TryGetDateTimeOffsetStaticProperty(string memberName, out object? value)
    {
        value = memberName.ToLowerInvariant() switch
        {
            "now" => DateTimeOffset.Now,
            "utcnow" => DateTimeOffset.UtcNow,
            "minvalue" => DateTimeOffset.MinValue,
            "maxvalue" => DateTimeOffset.MaxValue,
            _ => null
        };

        return value is not null;
    }

    private static bool TryGetTimeSpanStaticProperty(string memberName, out object? value)
    {
        value = memberName.ToLowerInvariant() switch
        {
            "zero" => TimeSpan.Zero,
            "minvalue" => TimeSpan.MinValue,
            "maxvalue" => TimeSpan.MaxValue,
            _ => null
        };

        return value is not null;
    }

    private static bool TryGetDateTimeInstanceMember(DateTime value, string memberName, out object? member)
    {
        member = memberName.ToLowerInvariant() switch
        {
            "date" => value.Date,
            "day" => value.Day,
            "dayofweek" => value.DayOfWeek.ToString(),
            "dayofyear" => value.DayOfYear,
            "hour" => value.Hour,
            "kind" => value.Kind.ToString(),
            "millisecond" => value.Millisecond,
            "minute" => value.Minute,
            "month" => value.Month,
            "second" => value.Second,
            "ticks" => value.Ticks,
            "timeofday" => value.TimeOfDay,
            "year" => value.Year,
            _ => null
        };

        if (member is not null)
        {
            return true;
        }

        if (IsMember(memberName, "AddDays", "AddHours", "AddMinutes", "AddMonths", "AddSeconds", "AddYears",
            "ToLocalTime", "ToString", "ToUniversalTime"))
        {
            member = new PowerShellWasmDotNetMethod(DateTimeType, memberName, value, IsStatic: false);
            return true;
        }

        return false;
    }

    private static bool TryGetDateTimeOffsetInstanceMember(DateTimeOffset value, string memberName, out object? member)
    {
        member = memberName.ToLowerInvariant() switch
        {
            "date" => value.Date,
            "datetime" => value.DateTime,
            "day" => value.Day,
            "dayofweek" => value.DayOfWeek.ToString(),
            "dayofyear" => value.DayOfYear,
            "hour" => value.Hour,
            "localdatetime" => value.LocalDateTime,
            "millisecond" => value.Millisecond,
            "minute" => value.Minute,
            "month" => value.Month,
            "offset" => value.Offset,
            "second" => value.Second,
            "ticks" => value.Ticks,
            "timeofday" => value.TimeOfDay,
            "utcdatetime" => value.UtcDateTime,
            "year" => value.Year,
            _ => null
        };

        if (member is not null)
        {
            return true;
        }

        if (IsMember(memberName, "AddDays", "AddHours", "AddMinutes", "AddMonths", "AddSeconds", "AddYears",
            "ToLocalTime", "ToString", "ToUniversalTime"))
        {
            member = new PowerShellWasmDotNetMethod(DateTimeOffsetType, memberName, value, IsStatic: false);
            return true;
        }

        return false;
    }

    private static bool TryGetTimeSpanInstanceMember(TimeSpan value, string memberName, out object? member)
    {
        member = memberName.ToLowerInvariant() switch
        {
            "days" => value.Days,
            "hours" => value.Hours,
            "milliseconds" => value.Milliseconds,
            "minutes" => value.Minutes,
            "seconds" => value.Seconds,
            "ticks" => value.Ticks,
            "totaldays" => value.TotalDays,
            "totalhours" => value.TotalHours,
            "totalmilliseconds" => value.TotalMilliseconds,
            "totalminutes" => value.TotalMinutes,
            "totalseconds" => value.TotalSeconds,
            _ => null
        };

        if (member is not null)
        {
            return true;
        }

        if (IsMember(memberName, "ToString"))
        {
            member = new PowerShellWasmDotNetMethod(TimeSpanType, memberName, value, IsStatic: false);
            return true;
        }

        return false;
    }

    private static bool TryGetTimeZoneInfoInstanceMember(TimeZoneInfo value, string memberName, out object? member)
    {
        member = memberName.ToLowerInvariant() switch
        {
            "baseutcoffset" => value.BaseUtcOffset,
            "daylightname" => value.DaylightName,
            "displayname" => value.DisplayName,
            "id" => value.Id,
            "standardname" => value.StandardName,
            "supportsdaylightsavingtime" => value.SupportsDaylightSavingTime,
            _ => null
        };

        if (member is not null)
        {
            return true;
        }

        if (IsMember(memberName, "IsDaylightSavingTime", "ToString"))
        {
            member = new PowerShellWasmDotNetMethod(TimeZoneInfoType, memberName, value, IsStatic: false);
            return true;
        }

        return false;
    }

    private static bool TryGetListInstanceMember(IList list, string memberName, out object? member)
    {
        member = memberName.ToLowerInvariant() switch
        {
            "isfixedsize" => list.IsFixedSize,
            "isreadonly" => list.IsReadOnly,
            _ => null
        };

        if (member is not null)
        {
            return true;
        }

        if (IsMember(memberName, "Add", "Clear", "Contains", "Insert", "Remove", "RemoveAt", "ToArray"))
        {
            member = new PowerShellWasmDotNetMethod(
                list is ArrayList ? ArrayListType : GenericListTypePrefix,
                memberName,
                list,
                IsStatic: false);
            return true;
        }

        return false;
    }

    private static object InvokeMath(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments) =>
        method.Name.ToLowerInvariant() switch
        {
            "abs" => InvokeUnaryMath(method, arguments, Math.Abs),
            "ceiling" => InvokeUnaryMath(method, arguments, Math.Ceiling),
            "floor" => InvokeUnaryMath(method, arguments, Math.Floor),
            "round" => InvokeRound(method, arguments),
            "truncate" => InvokeUnaryMath(method, arguments, Math.Truncate),
            "sqrt" => InvokeUnaryMath(method, arguments, Math.Sqrt),
            "pow" => InvokeBinaryMath(method, arguments, Math.Pow),
            "max" => InvokeBinaryMath(method, arguments, Math.Max),
            "min" => InvokeBinaryMath(method, arguments, Math.Min),
            _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
        };

    private static object InvokeUnaryMath(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<double, double> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return NormalizeNumber(invoke(ToDouble(arguments[0])));
    }

    private static object InvokeBinaryMath(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<double, double, double> invoke)
    {
        RequireArgumentCount(method, arguments, 2);
        return NormalizeNumber(invoke(ToDouble(arguments[0]), ToDouble(arguments[1])));
    }

    private static object InvokeRound(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 1)
        {
            return NormalizeNumber(Math.Round(ToDouble(arguments[0])));
        }

        if (arguments.Count == 2)
        {
            return NormalizeNumber(Math.Round(ToDouble(arguments[0]), ToInt32(arguments[1])));
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 1 or 2 argument(s), but received {arguments.Count}.");
    }

    private static object InvokeDateTime(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (method.IsStatic)
        {
            return method.Name.ToLowerInvariant() switch
            {
                "parse" => InvokeStaticParse(method, arguments, ToDateTime),
                "specifykind" => InvokeSpecifyKind(method, arguments),
                "isleapyear" => InvokeIsLeapYear(method, arguments),
                "daysinmonth" => InvokeDaysInMonth(method, arguments),
                _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
            };
        }

        var dateTime = (DateTime)method.Target!;
        return method.Name.ToLowerInvariant() switch
        {
            "adddays" => InvokeDateTimeDouble(method, arguments, dateTime.AddDays),
            "addhours" => InvokeDateTimeDouble(method, arguments, dateTime.AddHours),
            "addminutes" => InvokeDateTimeDouble(method, arguments, dateTime.AddMinutes),
            "addmonths" => InvokeDateTimeInt(method, arguments, dateTime.AddMonths),
            "addseconds" => InvokeDateTimeDouble(method, arguments, dateTime.AddSeconds),
            "addyears" => InvokeDateTimeInt(method, arguments, dateTime.AddYears),
            "tolocaltime" => InvokeNoArgument(method, arguments, dateTime.ToLocalTime),
            "tostring" => InvokeToString(method, arguments, dateTime),
            "touniversaltime" => InvokeNoArgument(method, arguments, dateTime.ToUniversalTime),
            _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
        };
    }

    private static object InvokeDateTimeOffset(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (method.IsStatic)
        {
            return method.Name.ToLowerInvariant() switch
            {
                "parse" => InvokeStaticParse(method, arguments, ToDateTimeOffset),
                _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
            };
        }

        var dateTimeOffset = (DateTimeOffset)method.Target!;
        return method.Name.ToLowerInvariant() switch
        {
            "adddays" => InvokeDateTimeOffsetDouble(method, arguments, dateTimeOffset.AddDays),
            "addhours" => InvokeDateTimeOffsetDouble(method, arguments, dateTimeOffset.AddHours),
            "addminutes" => InvokeDateTimeOffsetDouble(method, arguments, dateTimeOffset.AddMinutes),
            "addmonths" => InvokeDateTimeOffsetInt(method, arguments, dateTimeOffset.AddMonths),
            "addseconds" => InvokeDateTimeOffsetDouble(method, arguments, dateTimeOffset.AddSeconds),
            "addyears" => InvokeDateTimeOffsetInt(method, arguments, dateTimeOffset.AddYears),
            "tolocaltime" => InvokeNoArgument(method, arguments, dateTimeOffset.ToLocalTime),
            "tostring" => InvokeToString(method, arguments, dateTimeOffset),
            "touniversaltime" => InvokeNoArgument(method, arguments, dateTimeOffset.ToUniversalTime),
            _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
        };
    }

    private static object InvokeTimeSpan(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (method.IsStatic)
        {
            return method.Name.ToLowerInvariant() switch
            {
                "parse" => InvokeStaticParse(method, arguments, ToTimeSpan),
                "fromdays" => InvokeTimeSpanDouble(method, arguments, TimeSpan.FromDays),
                "fromhours" => InvokeTimeSpanDouble(method, arguments, TimeSpan.FromHours),
                "fromminutes" => InvokeTimeSpanDouble(method, arguments, TimeSpan.FromMinutes),
                "fromseconds" => InvokeTimeSpanDouble(method, arguments, TimeSpan.FromSeconds),
                "frommilliseconds" => InvokeTimeSpanDouble(method, arguments, TimeSpan.FromMilliseconds),
                _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
            };
        }

        var timeSpan = (TimeSpan)method.Target!;
        return method.Name.ToLowerInvariant() switch
        {
            "tostring" => InvokeToString(method, arguments, timeSpan),
            _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
        };
    }

    private static object InvokeTimeZoneInfo(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (method.IsStatic)
        {
            return method.Name.ToLowerInvariant() switch
            {
                "findsystemtimezonebyid" => InvokeFindSystemTimeZoneById(method, arguments),
                "converttime" => InvokeConvertTime(method, arguments),
                "converttimetoutc" => InvokeConvertTimeToUtc(method, arguments),
                "converttimefromutc" => InvokeConvertTimeFromUtc(method, arguments),
                "converttimebysystemtimezoneid" => InvokeConvertTimeBySystemTimeZoneId(method, arguments),
                _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
            };
        }

        var timeZone = (TimeZoneInfo)method.Target!;
        return method.Name.ToLowerInvariant() switch
        {
            "isdaylightsavingtime" => InvokeTimeZoneIsDaylightSavingTime(method, arguments, timeZone),
            "tostring" => InvokeToString(method, arguments, timeZone),
            _ => throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.")
        };
    }

    private static object? InvokeList(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments, IList list)
    {
        switch (method.Name.ToLowerInvariant())
        {
            case "add":
                RequireArgumentCount(method, arguments, 1);
                return AddToList(list, arguments[0]);
            case "clear":
                RequireArgumentCount(method, arguments, 0);
                list.Clear();
                return null;
            case "contains":
                RequireArgumentCount(method, arguments, 1);
                return list.Contains(ConvertListItemForExistingList(list, arguments[0]));
            case "insert":
                RequireArgumentCount(method, arguments, 2);
                list.Insert(ToInt32(arguments[0]), ConvertListItemForExistingList(list, arguments[1]));
                return null;
            case "remove":
                RequireArgumentCount(method, arguments, 1);
                list.Remove(ConvertListItemForExistingList(list, arguments[0]));
                return null;
            case "removeat":
                RequireArgumentCount(method, arguments, 1);
                list.RemoveAt(ToInt32(arguments[0]));
                return null;
            case "toarray":
                RequireArgumentCount(method, arguments, 0);
                return list.Cast<object?>().ToArray();
            default:
                throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.");
        }
    }

    private static object? AddToList(IList list, object? value)
    {
        if (list is ArrayList arrayList)
        {
            return arrayList.Add(value);
        }

        list.Add(ConvertListItemForExistingList(list, value));
        return null;
    }

    private static object? ConvertListItemForExistingList(IList list, object? value) =>
        list switch
        {
            List<string> => ToInvariantString(value),
            List<int> => ToInt32(value),
            List<long> => ToInt64(value),
            List<double> => ToDouble(value),
            List<decimal> => ToDecimal(value),
            List<bool> => ToBoolean(value),
            List<byte> => Convert.ToByte(value, CultureInfo.InvariantCulture),
            List<DateTime> => ToDateTime(value),
            _ => value
        };

    private static object InvokeStaticParse<T>(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<object?, T> parse)
    {
        RequireArgumentCount(method, arguments, 1);
        return parse(arguments[0])!;
    }

    private static object InvokeSpecifyKind(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        RequireArgumentCount(method, arguments, 2);
        return DateTime.SpecifyKind(ToDateTime(arguments[0]), ToDateTimeKind(arguments[1]));
    }

    private static object InvokeIsLeapYear(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        RequireArgumentCount(method, arguments, 1);
        return DateTime.IsLeapYear(ToInt32(arguments[0]));
    }

    private static object InvokeDaysInMonth(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        RequireArgumentCount(method, arguments, 2);
        return DateTime.DaysInMonth(ToInt32(arguments[0]), ToInt32(arguments[1]));
    }

    private static object InvokeDateTimeDouble(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<double, DateTime> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return invoke(ToDouble(arguments[0]));
    }

    private static object InvokeDateTimeInt(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<int, DateTime> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return invoke(ToInt32(arguments[0]));
    }

    private static object InvokeDateTimeOffsetDouble(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<double, DateTimeOffset> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return invoke(ToDouble(arguments[0]));
    }

    private static object InvokeDateTimeOffsetInt(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<int, DateTimeOffset> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return invoke(ToInt32(arguments[0]));
    }

    private static object InvokeTimeSpanDouble(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<double, TimeSpan> invoke)
    {
        RequireArgumentCount(method, arguments, 1);
        return invoke(ToDouble(arguments[0]));
    }

    private static object InvokeFindSystemTimeZoneById(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        RequireArgumentCount(method, arguments, 1);
        return TimeZoneInfo.FindSystemTimeZoneById(ToInvariantString(arguments[0]));
    }

    private static object InvokeConvertTime(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 2)
        {
            return TimeZoneInfo.ConvertTime(ToDateTime(arguments[0]), ToTimeZoneInfo(arguments[1]));
        }

        if (arguments.Count == 3)
        {
            return TimeZoneInfo.ConvertTime(
                ToDateTime(arguments[0]),
                ToTimeZoneInfo(arguments[1]),
                ToTimeZoneInfo(arguments[2]));
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 2 or 3 argument(s), but received {arguments.Count}.");
    }

    private static object InvokeConvertTimeToUtc(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 1)
        {
            return TimeZoneInfo.ConvertTimeToUtc(ToDateTime(arguments[0]));
        }

        if (arguments.Count == 2)
        {
            return TimeZoneInfo.ConvertTimeToUtc(ToDateTime(arguments[0]), ToTimeZoneInfo(arguments[1]));
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 1 or 2 argument(s), but received {arguments.Count}.");
    }

    private static object InvokeConvertTimeFromUtc(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments)
    {
        RequireArgumentCount(method, arguments, 2);
        return TimeZoneInfo.ConvertTimeFromUtc(ToDateTime(arguments[0]), ToTimeZoneInfo(arguments[1]));
    }

    private static object InvokeConvertTimeBySystemTimeZoneId(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 2)
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(ToDateTime(arguments[0]), ToInvariantString(arguments[1]));
        }

        if (arguments.Count == 3)
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                ToDateTime(arguments[0]),
                ToInvariantString(arguments[1]),
                ToInvariantString(arguments[2]));
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 2 or 3 argument(s), but received {arguments.Count}.");
    }

    private static object InvokeTimeZoneIsDaylightSavingTime(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        TimeZoneInfo timeZone)
    {
        RequireArgumentCount(method, arguments, 1);
        return timeZone.IsDaylightSavingTime(ToDateTime(arguments[0]));
    }

    private static object InvokeNoArgument<T>(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        Func<T> invoke)
    {
        RequireArgumentCount(method, arguments, 0);
        return invoke()!;
    }

    private static object InvokeToString(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        IFormattable value)
    {
        if (arguments.Count == 0)
        {
            return value.ToString(null, CultureInfo.InvariantCulture);
        }

        if (arguments.Count == 1)
        {
            return value.ToString(ToInvariantString(arguments[0]), CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException(
            $"Method '{method.Name}' expects 0 or 1 argument(s), but received {arguments.Count}.");
    }

    private static object InvokeToString(
        PowerShellWasmDotNetMethod method,
        IReadOnlyList<object?> arguments,
        TimeZoneInfo value)
    {
        RequireArgumentCount(method, arguments, 0);
        return value.ToString();
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

    private static long ToInt64(object? value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object? value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object? value) =>
        Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool ToBoolean(object? value) =>
        value switch
        {
            null => false,
            bool boolValue => boolValue,
            string text => text.Length > 0,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };

    private static DateTime ToDateTime(object? value) =>
        value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
            null => default,
            _ => DateTime.Parse(ToInvariantString(value), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind)
        };

    private static DateTimeOffset ToDateTimeOffset(object? value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            null => default,
            _ => DateTimeOffset.Parse(ToInvariantString(value), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind)
        };

    private static TimeSpan ToTimeSpan(object? value) =>
        value switch
        {
            TimeSpan timeSpan => timeSpan,
            null => TimeSpan.Zero,
            _ => TimeSpan.Parse(ToInvariantString(value), CultureInfo.InvariantCulture)
        };

    private static TimeZoneInfo ToTimeZoneInfo(object? value) =>
        value switch
        {
            TimeZoneInfo timeZoneInfo => timeZoneInfo,
            _ => TimeZoneInfo.FindSystemTimeZoneById(ToInvariantString(value))
        };

    private static DateTimeKind ToDateTimeKind(object? value)
    {
        if (value is DateTimeKind kind)
        {
            return kind;
        }

        return Enum.TryParse<DateTimeKind>(ToInvariantString(value), ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Value '{value}' cannot be converted to DateTimeKind.");
    }

    private static object NormalizeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0000000001 && value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(Math.Round(value), CultureInfo.InvariantCulture)
            : value;
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
