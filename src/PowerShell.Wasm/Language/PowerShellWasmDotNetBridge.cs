using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
#if PSWASM_CRYPTO
using System.Security.Cryptography;
using System.Text;
#endif

namespace PSWasm.Language;

// Browser-safe equivalent of PowerShell's collection-filter selection modes.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L2598-L2625
internal enum PowerShellWasmWhereMode { Default, First, Last, SkipUntil, Until, Split }

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
    private const string DoubleType = "System.Double";
    private const string DateTimeType = "System.DateTime";
    private const string DateTimeOffsetType = "System.DateTimeOffset";
    private const string TimeSpanType = "System.TimeSpan";
    private const string TimeZoneInfoType = "System.TimeZoneInfo";
    private const string ArrayType = "System.Array";
    private const string HashtableType = "System.Collections.Hashtable";
    private const string DictionaryType = "System.Collections.IDictionary";
    private const string OrderedDictionaryType = "System.Collections.Specialized.OrderedDictionary";
    private const string EnumeratorType = "System.Collections.IEnumerator";
    private const string EnumerableType = "System.Collections.IEnumerable";
    private const string ObjectType = "System.Object";
    private const string CustomObjectType = "System.Management.Automation.PSCustomObject";
    private const string WhereModeType = "System.Management.Automation.WhereOperatorSelectionMode";
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
        // Core numeric accelerators resolve only to this bridge's explicit value-type allowlist.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/TypeResolver.cs#L732-L783
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, fullName) in new[]
        {
            ("object", ObjectType), ("string", StringType), ("char", "System.Char"), ("bool", "System.Boolean"),
            ("int", "System.Int32"), ("long", "System.Int64"), ("decimal", "System.Decimal"), ("byte", "System.Byte"),
            ("sbyte", "System.SByte"), ("short", "System.Int16"), ("ushort", "System.UInt16"),
            ("uint", "System.UInt32"), ("ulong", "System.UInt64"), ("float", "System.Single"),
            ("bigint", "System.Numerics.BigInteger"),
            ("pscustomobject", CustomObjectType)
        })
        {
            aliases[alias] = fullName;
            aliases[fullName] = fullName;
        }
        aliases["boolean"] = "System.Boolean";
        aliases["int32"] = "System.Int32";
        aliases["int64"] = "System.Int64";
        aliases["int16"] = "System.Int16";
        aliases["uint16"] = "System.UInt16";
        aliases["uint32"] = "System.UInt32";
        aliases["uint64"] = "System.UInt64";
        aliases["single"] = "System.Single";
        aliases["Numerics.BigInteger"] = "System.Numerics.BigInteger";
        aliases[WhereModeType] = WhereModeType;
        aliases["Math"] = MathType;
        aliases["System.Math"] = MathType;
        aliases["double"] = DoubleType;
        aliases[DoubleType] = DoubleType;
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
        // PowerShell's core type accelerators; the browser bridge recognizes only its supported runtime values.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/TypeResolver.cs#L732-L746
        aliases["array"] = ArrayType;
        aliases["System.Array"] = ArrayType;
        aliases["hashtable"] = HashtableType;
        aliases["System.Collections.Hashtable"] = HashtableType;
        aliases["ordered"] = OrderedDictionaryType;
        aliases["Collections.Specialized.OrderedDictionary"] = OrderedDictionaryType;
        aliases[OrderedDictionaryType] = OrderedDictionaryType;
        aliases["Collections.IDictionary"] = DictionaryType;
        aliases[DictionaryType] = DictionaryType;
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

        if (fullName is HashtableType or OrderedDictionaryType)
        {
            value = ConstructDictionary(fullName, arguments);
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

        if (fullName == WhereModeType)
        {
            value = ConvertWhereMode(input);
            return true;
        }

        if (fullName is HashtableType or OrderedDictionaryType or DictionaryType)
        {
            if (input is null) return true;
            if (input is not IDictionary dictionary || input is PowerShellWasmCustomObject)
                throw new InvalidOperationException($"Cannot convert this value to [{fullName}].");
            // Native IDictionary-to-Hashtable conversion is a shallow copy with the platform default comparer.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs#L3384-L3392
            value = fullName switch
            {
                HashtableType => input is Hashtable ? input : new Hashtable(dictionary),
                DictionaryType => input,
                OrderedDictionaryType when input is OrderedDictionary => input,
                OrderedDictionaryType when dictionary.Count == 0 => new OrderedDictionary(),
                _ => throw new InvalidOperationException($"Cannot convert dictionary entries into properties of [{fullName}].")
            };
            return true;
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
            // Assignment-compatible references, including null, keep their identity before collection conversion.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs#L5643-L5657
            value = input is null or ArrayList ? input : new ArrayList(EnumerateForCollection(input).ToArray());
            return true;
        }

        if (IsGenericListType(fullName))
        {
            value = input is null || GenericListTypeMatches(fullName, input)
                ? input : ConstructGenericList(fullName, EnumerateForCollection(input).ToArray());
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
            DoubleType => input is double,
            ObjectType => PowerShellWasmOutput.AsNull(input) is not null,
            StringType => input is string,
            "System.Char" => input is char,
            "System.Boolean" => input is bool,
            "System.Int32" => input is int,
            "System.Int64" => input is long,
            "System.Decimal" => input is decimal,
            "System.Byte" => input is byte,
            "System.SByte" => input is sbyte,
            "System.Int16" => input is short,
            "System.UInt16" => input is ushort,
            "System.UInt32" => input is uint,
            "System.UInt64" => input is ulong,
            "System.Single" => input is float,
            "System.Numerics.BigInteger" => input is System.Numerics.BigInteger,
            CustomObjectType => input is PowerShellWasmCustomObject,
            WhereModeType => input is PowerShellWasmWhereMode,
            "System.Object[]" => input is object[],
            "System.String[]" => input is string[],
            "System.Char[]" => input?.GetType() == typeof(char[]),
            "System.Boolean[]" => input?.GetType() == typeof(bool[]),
            "System.Int32[]" => input?.GetType() == typeof(int[]),
            "System.Int64[]" => input?.GetType() == typeof(long[]),
            "System.Double[]" => input?.GetType() == typeof(double[]),
            "System.Byte[]" => input?.GetType() == typeof(byte[]),
            "System.SByte[]" => input?.GetType() == typeof(sbyte[]),
            "System.Int16[]" => input?.GetType() == typeof(short[]),
            "System.UInt16[]" => input?.GetType() == typeof(ushort[]),
            "System.UInt32[]" => input?.GetType() == typeof(uint[]),
            "System.UInt64[]" => input?.GetType() == typeof(ulong[]),
            "System.Single[]" => input?.GetType() == typeof(float[]),
            "System.Decimal[]" => input?.GetType() == typeof(decimal[]),
            "System.Numerics.BigInteger[]" => input?.GetType() == typeof(System.Numerics.BigInteger[]),
            DateTimeType => input is DateTime,
            DateTimeOffsetType => input is DateTimeOffset,
            TimeSpanType => input is TimeSpan,
            TimeZoneInfoType => input is TimeZoneInfo,
            ArrayType => input is Array,
            HashtableType => input is Hashtable,
            OrderedDictionaryType => input is OrderedDictionary,
            DictionaryType => input is IDictionary and not PowerShellWasmCustomObject,
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

        if (type.FullName == WhereModeType && Enum.GetNames<PowerShellWasmWhereMode>()
            .Any(name => name.Equals(memberName, StringComparison.OrdinalIgnoreCase)))
        {
            value = Enum.Parse<PowerShellWasmWhereMode>(memberName, ignoreCase: true);
            return true;
        }

        // Explicit primitive bounds let numeric scripts reason about width without reflection or arbitrary member access.
        if (IsMember(memberName, "MinValue", "MaxValue"))
        {
            var minimum = memberName.Equals("MinValue", StringComparison.OrdinalIgnoreCase);
            value = type.FullName switch
            {
                "System.SByte" => minimum ? sbyte.MinValue : sbyte.MaxValue,
                "System.Byte" => minimum ? byte.MinValue : byte.MaxValue,
                "System.Int16" => minimum ? short.MinValue : short.MaxValue,
                "System.UInt16" => minimum ? ushort.MinValue : ushort.MaxValue,
                "System.Int32" => minimum ? int.MinValue : int.MaxValue,
                "System.UInt32" => minimum ? uint.MinValue : uint.MaxValue,
                "System.Int64" => minimum ? long.MinValue : long.MaxValue,
                "System.UInt64" => minimum ? ulong.MinValue : ulong.MaxValue,
                "System.Single" => minimum ? float.MinValue : float.MaxValue,
                DoubleType => minimum ? double.MinValue : double.MaxValue,
                "System.Decimal" => minimum ? decimal.MinValue : decimal.MaxValue,
                _ => null
            };
            if (value is not null) return true;
        }

        if ((type.FullName is HashtableType or OrderedDictionaryType) && memberName.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(type.FullName, memberName, null, IsStatic: true);
            return true;
        }

        // Preserve the CLR's non-finite numeric values so ordinary conversions decide how an operation handles them.
        // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/Double.cs#L43-L45
        if (type.FullName == DoubleType && IsMember(memberName, "NaN", "PositiveInfinity", "NegativeInfinity"))
        {
            value = memberName.ToLowerInvariant() switch
            {
                "nan" => double.NaN,
                "positiveinfinity" => double.PositiveInfinity,
                _ => double.NegativeInfinity
            };
            return true;
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
        if (target is PowerShellWasmDotNetType descriptor)
        {
            if (memberName.Equals("FullName", StringComparison.OrdinalIgnoreCase)) value = descriptor.FullName;
            else if (memberName.Equals("Name", StringComparison.OrdinalIgnoreCase)) value = GetSimpleTypeName(descriptor.FullName);
            else return false;
            return true;
        }

        if (memberName.Equals("GetType", StringComparison.OrdinalIgnoreCase) && GetKnownRuntimeTypeName(target) is not null)
        {
            value = new PowerShellWasmDotNetMethod(ObjectType, memberName, target, IsStatic: false);
            return true;
        }

        if (memberName.Equals("ToString", StringComparison.OrdinalIgnoreCase) &&
            PowerShellWasmAstExecutor.IsComparisonNumber(target))
        {
            value = new PowerShellWasmDotNetMethod(ObjectType, memberName, target, IsStatic: false);
            return true;
        }

        if (target is DictionaryEntry entry)
        {
            if (IsMember(memberName, "Key", "Name")) value = entry.Key;
            else if (memberName.Equals("Value", StringComparison.OrdinalIgnoreCase)) value = entry.Value;
            else return false;
            return true;
        }

        if (target is IDictionary dictionary && (target is Hashtable or OrderedDictionary) &&
            TryGetDictionaryInstanceMember(dictionary, memberName, out value)) return true;

        // Use the collection's actual iterator, retaining shared position and concrete identity; do not enumerate a copy.
        // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/Array.cs#L3062-L3065
        // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/Collections/Hashtable.cs#L1180-L1240
        if (target is ICollection and not PowerShellWasmCustomObject &&
            memberName.Equals("GetEnumerator", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(EnumerableType, memberName, target, IsStatic: false);
            return true;
        }

        // Collection Object.ToString is a direct method, so it takes precedence over item-method enumeration.
        // Scalar formatting overloads remain explicit and no reflected member discovery is exposed.
        // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/Object.cs#L37-L43
        if (target is ICollection or IEnumerator && target is not PowerShellWasmCustomObject &&
            memberName.Equals("ToString", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(ObjectType, memberName, target, IsStatic: false);
            return true;
        }

        if (target is IEnumerator enumerator)
        {
            // PowerShell property getters yield null when an enumerator is before its first or after its last item.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L5580-L5594
            try
            {
                if (memberName.Equals("Current", StringComparison.OrdinalIgnoreCase)) value = enumerator.Current;
                else if (enumerator is IDictionaryEnumerator entries && IsMember(memberName, "Entry", "Key", "Value"))
                    value = memberName.ToLowerInvariant() switch { "entry" => entries.Entry, "key" => entries.Key, _ => entries.Value };
                else if (IsMember(memberName, "MoveNext", "Reset"))
                    value = new PowerShellWasmDotNetMethod(EnumeratorType, memberName, target, IsStatic: false);
                else return false;
            }
            catch (InvalidOperationException) { value = null; }
            return true;
        }
#if PSWASM_CRYPTO
        if (target is Encoding && memberName.Equals("GetBytes", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(EncodingType, memberName, target, IsStatic: false);
            return true;
        }
#endif

        if (target is string && IsMember(memberName, "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant", "ToString", "Trim", "Substring"))
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

        if (target is IReadOnlyDictionary<string, object?> or IDictionary<string, object?> && target is not OrderedDictionary &&
            memberName.Equals("ContainsKey", StringComparison.OrdinalIgnoreCase))
        {
            value = new PowerShellWasmDotNetMethod(DictionaryType, memberName, target, IsStatic: false);
            return true;
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
        if (method.IsStatic && method.Name.Equals("new", StringComparison.OrdinalIgnoreCase) &&
            method.DeclaringType is HashtableType or OrderedDictionaryType)
            return ConstructDictionary(method.DeclaringType, arguments);

        if (method.DeclaringType == ObjectType && method.Name.Equals("GetType", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 0);
            return new PowerShellWasmDotNetType(GetKnownRuntimeTypeName(method.Target!)!);
        }

        if (method.DeclaringType == ObjectType && method.Name.Equals("ToString", StringComparison.OrdinalIgnoreCase))
        {
            if (method.Target is IFormattable number && PowerShellWasmAstExecutor.IsComparisonNumber(method.Target))
            {
                if (arguments.Count > 1) throw new InvalidOperationException("Numeric ToString accepts an optional format string.");
                var format = arguments.Count == 0 ? null : ToInvariantString(arguments[0]);
                return number.ToString(format, CultureInfo.InvariantCulture);
            }
            RequireArgumentCount(method, arguments, 0);
            return method.Target!.ToString();
        }

        if (method.DeclaringType == EnumerableType && method.Target is ICollection collection)
        {
            RequireArgumentCount(method, arguments, 0);
            return collection.GetEnumerator();
        }

        if (method.DeclaringType == EnumeratorType && method.Target is IEnumerator enumerator)
        {
            RequireArgumentCount(method, arguments, 0);
            if (method.Name.Equals("MoveNext", StringComparison.OrdinalIgnoreCase)) return enumerator.MoveNext();
            enumerator.Reset();
            return PowerShellWasmOutput.NoOutput;
        }

        if (method.DeclaringType == DictionaryType && method.Target is IDictionary rawDictionary &&
            method.Target is Hashtable or OrderedDictionary)
            return InvokeDictionary(method, arguments, rawDictionary);
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
            // Parameterless casing follows the current culture; invariant variants remain explicit.
            // https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs#L2294-L2322
            return method.Name.ToLowerInvariant() switch
            {
                "tolower" => InvokeStringNoArgument(method, arguments, () => text.ToLower(CultureInfo.CurrentCulture)),
                "toupper" => InvokeStringNoArgument(method, arguments, () => text.ToUpper(CultureInfo.CurrentCulture)),
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

        if (method.DeclaringType.Equals(DictionaryType, StringComparison.OrdinalIgnoreCase) &&
            method.Name.Equals("ContainsKey", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(method, arguments, 1);
            var key = arguments[0];
            ArgumentNullException.ThrowIfNull(key);
            return method.Target switch
            {
                IReadOnlyDictionary<string, object?> dictionary => dictionary.ContainsKey(ToInvariantString(key)),
                IDictionary<string, object?> dictionary => dictionary.ContainsKey(ToInvariantString(key)),
                IDictionary dictionary => dictionary.Contains(key),
                _ => throw new InvalidOperationException("ContainsKey requires a supported dictionary.")
            };
        }

        if ((method.DeclaringType.Equals(ArrayListType, StringComparison.OrdinalIgnoreCase) ||
            method.DeclaringType.Equals(GenericListTypePrefix, StringComparison.OrdinalIgnoreCase)) &&
            method.Target is IList list)
        {
            return InvokeList(method, arguments, list);
        }

        throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.");
    }

    // Only descriptors for runtime values and collection interfaces are exposed, never reflected Type instances or member discovery.
    private static string? GetKnownRuntimeTypeName(object value) => value switch
    {
        string[] => "System.String[]",
        bool[] when value.GetType() == typeof(bool[]) => "System.Boolean[]",
        char[] when value.GetType() == typeof(char[]) => "System.Char[]",
        byte[] when value.GetType() == typeof(byte[]) => "System.Byte[]",
        sbyte[] when value.GetType() == typeof(sbyte[]) => "System.SByte[]",
        short[] when value.GetType() == typeof(short[]) => "System.Int16[]",
        ushort[] when value.GetType() == typeof(ushort[]) => "System.UInt16[]",
        int[] when value.GetType() == typeof(int[]) => "System.Int32[]",
        uint[] when value.GetType() == typeof(uint[]) => "System.UInt32[]",
        long[] when value.GetType() == typeof(long[]) => "System.Int64[]",
        ulong[] when value.GetType() == typeof(ulong[]) => "System.UInt64[]",
        float[] when value.GetType() == typeof(float[]) => "System.Single[]",
        double[] when value.GetType() == typeof(double[]) => "System.Double[]",
        decimal[] when value.GetType() == typeof(decimal[]) => "System.Decimal[]",
        System.Numerics.BigInteger[] when value.GetType() == typeof(System.Numerics.BigInteger[]) => "System.Numerics.BigInteger[]",
        DateTime[] => "System.DateTime[]",
        DateTimeOffset[] => "System.DateTimeOffset[]",
        TimeSpan[] => "System.TimeSpan[]",
        object[] array when array.GetType() == typeof(object[]) => "System.Object[]",
        OrderedDictionary => OrderedDictionaryType,
        Hashtable => HashtableType,
        PowerShellWasmCustomObject => "System.Management.Automation.PSCustomObject",
        DictionaryEntry => "System.Collections.DictionaryEntry",
        string => StringType,
        bool => "System.Boolean",
        char => "System.Char",
        byte => "System.Byte",
        sbyte => "System.SByte",
        short => "System.Int16",
        ushort => "System.UInt16",
        int => "System.Int32",
        uint => "System.UInt32",
        long => "System.Int64",
        ulong => "System.UInt64",
        float => "System.Single",
        double => "System.Double",
        decimal => "System.Decimal",
        System.Numerics.BigInteger => "System.Numerics.BigInteger",
        DateTime => DateTimeType,
        DateTimeOffset => DateTimeOffsetType,
        TimeSpan => TimeSpanType,
        TimeZoneInfo => TimeZoneInfoType,
        PowerShellWasmWhereMode => WhereModeType,
        IEnumerator or ICollection => value.GetType().FullName,
        _ => null
    };

    private static string GetSimpleTypeName(string fullName)
    {
        // CLR nested collection types use '+'; generic argument namespaces do not belong to the simple type name.
        var name = fullName.Contains('`') && fullName.IndexOf('[') is var arguments && arguments >= 0
            ? fullName[..arguments] : fullName;
        return name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1)..];
    }

    private static object ConstructDictionary(string typeName, IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
            return typeName == HashtableType ? new Hashtable() : new OrderedDictionary();
        if (arguments.Count == 1)
        {
            if (typeName == HashtableType && arguments[0] is IDictionary dictionary and not PowerShellWasmCustomObject)
                return new Hashtable(dictionary);
            var capacity = ToInt32(arguments[0]);
            return typeName == HashtableType ? new Hashtable(capacity) : new OrderedDictionary(capacity);
        }
        throw new InvalidOperationException($"Constructor for [{typeName}] expects zero arguments or one capacity argument.");
    }

    private static bool TryGetDictionaryInstanceMember(IDictionary dictionary, string name, out object? value)
    {
        value = name.ToLowerInvariant() switch
        {
            "count" => dictionary.Count,
            "keys" => dictionary.Keys,
            "values" => dictionary.Values,
            "isreadonly" => dictionary.IsReadOnly,
            "isfixedsize" => dictionary.IsFixedSize,
            "issynchronized" => dictionary.IsSynchronized,
            _ => null
        };
        if (value is not null) return true;
        var common = IsMember(name, "Contains", "Add", "Remove", "Clear", "GetEnumerator", "ToString");
        var ordered = dictionary is OrderedDictionary && IsMember(name, "Insert", "RemoveAt");
        var hashtable = dictionary is Hashtable && IsMember(name, "ContainsKey", "ContainsValue", "Clone");
        if (!common && !ordered && !hashtable) return false;
        value = new PowerShellWasmDotNetMethod(DictionaryType, name, dictionary, IsStatic: false);
        return true;
    }

    // Delegate the allowlisted dictionary operations to the same platform collections used by PowerShell literals.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6520-L6536
    // Mutating methods return no pipeline output; keys are never converted to strings by this bridge.
    private static object? InvokeDictionary(PowerShellWasmDotNetMethod method, IReadOnlyList<object?> arguments, IDictionary dictionary)
    {
        // Method binding converts no-output arguments to null; literal and index storage retain their value semantics.
        arguments = arguments.Select(PowerShellWasmOutput.AsNull).ToArray();
        switch (method.Name.ToLowerInvariant())
        {
            case "contains":
            case "containskey":
                RequireArgumentCount(method, arguments, 1);
                return dictionary.Contains(arguments[0] ?? throw new ArgumentNullException("key"));
            case "containsvalue" when dictionary is Hashtable containsTable:
                RequireArgumentCount(method, arguments, 1);
                return containsTable.ContainsValue(arguments[0]);
            case "add":
                RequireArgumentCount(method, arguments, 2);
                dictionary.Add(arguments[0] ?? throw new ArgumentNullException("key"), arguments[1]);
                return PowerShellWasmOutput.NoOutput;
            case "insert" when dictionary is OrderedDictionary insertOrdered:
                RequireArgumentCount(method, arguments, 3);
                insertOrdered.Insert(ToInt32(arguments[0]), arguments[1] ?? throw new ArgumentNullException("key"), arguments[2]);
                return PowerShellWasmOutput.NoOutput;
            case "remove":
                RequireArgumentCount(method, arguments, 1);
                dictionary.Remove(arguments[0] ?? throw new ArgumentNullException("key"));
                return PowerShellWasmOutput.NoOutput;
            case "removeat" when dictionary is OrderedDictionary removeOrdered:
                RequireArgumentCount(method, arguments, 1);
                removeOrdered.RemoveAt(ToInt32(arguments[0]));
                return PowerShellWasmOutput.NoOutput;
            case "clear":
                RequireArgumentCount(method, arguments, 0);
                dictionary.Clear();
                return PowerShellWasmOutput.NoOutput;
            case "clone" when dictionary is Hashtable cloneTable:
                RequireArgumentCount(method, arguments, 0);
                return cloneTable.Clone();
            case "getenumerator":
                RequireArgumentCount(method, arguments, 0);
                return dictionary.GetEnumerator();
            case "tostring":
                RequireArgumentCount(method, arguments, 0);
                return dictionary is Hashtable ? HashtableType : dictionary.ToString();
            default:
                throw new InvalidOperationException($"Method '{method.Name}' is not available in this browser-safe runtime.");
        }
    }

    private static bool TryResolveTypeName(string typeName, out string fullName)
    {
        fullName = string.Empty;
        var trimmed = typeName.Trim();
        if (TryNormalizeGenericListType(trimmed, out fullName))
        {
            return true;
        }

        if (trimmed.EndsWith("[]", StringComparison.Ordinal) &&
            TypeAliases.TryGetValue(trimmed[..^2].Trim(), out var elementType) && elementType is
                ObjectType or StringType or "System.Char" or "System.Boolean" or "System.Int32" or "System.Int64" or DoubleType or "System.Byte"
                or "System.SByte" or "System.Int16" or "System.UInt16" or "System.UInt32" or "System.UInt64" or "System.Single"
                or "System.Decimal" or "System.Numerics.BigInteger")
        {
            fullName = elementType + "[]";
            return true;
        }

        return TypeAliases.TryGetValue(trimmed, out fullName!);
    }

    // Ordinary conversion targets produce Collection<T>; arrays and explicit generic lists are handled by the caller.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L2886-L2952
    // Use explicit closed generic types and the runtime's existing converter, without reflection or desktop type loading.
    internal static object CreateIntrinsicTypedCollection(string typeName, IEnumerable<object?> values, Func<object?, object?> convert)
    {
        if (!TryResolveTypeName(typeName, out var fullName))
            throw new InvalidOperationException($"Collection element type [{typeName}] is not available in this browser-safe runtime.");
        return fullName switch
        {
            ObjectType => Create<object?>(), StringType => Create<string?>(), "System.Char" => Create<char>(),
            "System.Boolean" => Create<bool>(), "System.Int32" => Create<int>(), "System.Int64" => Create<long>(),
            DoubleType => Create<double>(), "System.Decimal" => Create<decimal>(), "System.Byte" => Create<byte>(),
            "System.SByte" => Create<sbyte>(), "System.Int16" => Create<short>(), "System.UInt16" => Create<ushort>(),
            "System.UInt32" => Create<uint>(), "System.UInt64" => Create<ulong>(), "System.Single" => Create<float>(),
            "System.Numerics.BigInteger" => Create<System.Numerics.BigInteger>(),
            DateTimeType => Create<DateTime>(), DateTimeOffsetType => Create<DateTimeOffset>(), TimeSpanType => Create<TimeSpan>(),
            CustomObjectType => Create<PowerShellWasmCustomObject?>(), WhereModeType => Create<PowerShellWasmWhereMode>(),
            _ => throw new InvalidOperationException($"Collection element type [{typeName}] is not available in this browser-safe runtime.")
        };

        Collection<T> Create<T>()
        {
            var result = new Collection<T>();
            foreach (var item in values) result.Add((T)convert(item)!);
            return result;
        }
    }

    // PowerShell enum conversion accepts defined numeric values, case-insensitive names, and unambiguous prefixes.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/LanguagePrimitives.cs#L3551-L3645
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/EnumMinimumDisambiguation.cs#L37-L98
    internal static PowerShellWasmWhereMode ConvertWhereMode(object? value) =>
        ConvertWhereMode(value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static PowerShellWasmWhereMode ConvertWhereMode(object? value, HashSet<object> active)
    {
        value = PowerShellWasmOutput.AsNull(value);
        if (value is PowerShellWasmWhereMode mode) return mode;
        if (value is string text)
        {
            if (Enum.TryParse<PowerShellWasmWhereMode>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                return parsed;
            var names = Enum.GetNames<PowerShellWasmWhereMode>().Where(name => name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length == 1) return Enum.Parse<PowerShellWasmWhereMode>(names[0]);
        }
        else if (value is IEnumerable values)
        {
            // Native enum-array conversion combines raw string names; nonstrings convert recursively first.
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!active.Add(values)) throw new InvalidOperationException("A cyclic collection cannot be converted to a selection mode.");
            try
            {
                var names = values.Cast<object?>().Select(item => item is string name ? name : ConvertWhereMode(item, active).ToString());
                return ConvertWhereMode(string.Join(",", names), active);
            }
            finally { active.Remove(values); }
        }
        else if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
        {
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (number is >= 0 and <= 5) return (PowerShellWasmWhereMode)(int)number;
        }
        else if (value is float or double or decimal)
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (number is >= 0 and <= 5 && number == Math.Truncate(number)) return (PowerShellWasmWhereMode)(int)number;
        }
        throw new InvalidOperationException($"Value '{value}' cannot be converted to WhereOperatorSelectionMode.");
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

        if (IsMember(memberName, "Add", "Clear", "Contains", "Insert", "Remove", "RemoveAt") ||
            (list is ArrayList or List<string> or List<object?> or List<int> or List<long> or List<double> or
                List<decimal> or List<bool> or List<byte> or List<DateTime>) && memberName.Equals("ToArray", StringComparison.OrdinalIgnoreCase))
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

    // Behavioral guidance: PowerShell maps a void method result to AutomationNull, separately from a returned null.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Binding/Binders.cs#L6939-L6946
    // Browser-safe adaptation: preserve the declared returns of allowlisted collection methods without reflection.
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
                return PowerShellWasmOutput.NoOutput;
            case "contains":
                RequireArgumentCount(method, arguments, 1);
                return list.Contains(ConvertListItemForExistingList(list, arguments[0]));
            case "insert":
                RequireArgumentCount(method, arguments, 2);
                list.Insert(ToInt32(arguments[0]), ConvertListItemForExistingList(list, arguments[1]));
                return PowerShellWasmOutput.NoOutput;
            case "remove":
                RequireArgumentCount(method, arguments, 1);
                var item = ConvertListItemForExistingList(list, arguments[0]);
                var previousCount = list.Count;
                list.Remove(item);
                return list is ArrayList ? PowerShellWasmOutput.NoOutput : list.Count < previousCount;
            case "removeat":
                RequireArgumentCount(method, arguments, 1);
                list.RemoveAt(ToInt32(arguments[0]));
                return PowerShellWasmOutput.NoOutput;
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
        return PowerShellWasmOutput.NoOutput;
    }

    private static object? ConvertListItemForExistingList(IList list, object? value) =>
        list switch
        {
            List<string> or Collection<string> => ToInvariantString(value),
            List<int> or Collection<int> => PowerShellWasmAstExecutor.ConvertOptimizedValue("int", value, " "),
            List<long> or Collection<long> => PowerShellWasmAstExecutor.ConvertOptimizedValue("long", value, " "),
            List<double> or Collection<double> => PowerShellWasmAstExecutor.ConvertOptimizedValue("double", value, " "),
            List<decimal> or Collection<decimal> => PowerShellWasmAstExecutor.ConvertOptimizedValue("decimal", value, " "),
            List<bool> or Collection<bool> => ToBoolean(value),
            List<byte> or Collection<byte> => Convert.ToByte(value, CultureInfo.InvariantCulture),
            Collection<sbyte> => PowerShellWasmAstExecutor.ConvertOptimizedValue("sbyte", value, " "),
            Collection<short> => PowerShellWasmAstExecutor.ConvertOptimizedValue("short", value, " "),
            Collection<ushort> => PowerShellWasmAstExecutor.ConvertOptimizedValue("ushort", value, " "),
            Collection<uint> => PowerShellWasmAstExecutor.ConvertOptimizedValue("uint", value, " "),
            Collection<ulong> => PowerShellWasmAstExecutor.ConvertOptimizedValue("ulong", value, " "),
            Collection<float> => PowerShellWasmAstExecutor.ConvertOptimizedValue("float", value, " "),
            Collection<System.Numerics.BigInteger> => PowerShellWasmAstExecutor.ConvertOptimizedValue("bigint", value, " "),
            List<DateTime> or Collection<DateTime> => ToDateTime(value),
            Collection<char> => PowerShellWasmAstExecutor.ConvertOptimizedValue("char", value, " "),
            Collection<DateTimeOffset> => ToDateTimeOffset(value),
            Collection<TimeSpan> => ToTimeSpan(value),
            Collection<PowerShellWasmWhereMode> => ConvertWhereMode(value),
            Collection<PowerShellWasmCustomObject> =>
                PowerShellWasmAstExecutor.ConvertOptimizedValue(CustomObjectType, value, " "),
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
