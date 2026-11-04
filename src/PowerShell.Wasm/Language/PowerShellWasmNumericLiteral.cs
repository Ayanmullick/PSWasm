using System.Globalization;
using System.Numerics;

namespace PSWasm.Language;

// Behavioral adaptation of PowerShell's ScanNumberHelper / TryGetNumberValue, using browser-safe BCL numeric types.
// Preserve native suffix bounds, width-sensitive radix signs, multiplier order, and real-to-integer rounding.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/tokenizer.cs#L3555-L4162
internal static class PowerShellWasmNumericLiteral
{
    private enum Suffix { None, SignedByte, Byte, Short, UShort, Unsigned, Long, ULong, Decimal, BigInteger }
    private readonly record struct Parts(string Digits, int Radix, Suffix Suffix, bool Real, bool Negative, long Multiplier);

    internal static object Parse(string text) => TryParse(text, out var value)
        ? value : throw new InvalidOperationException($"The numeric constant {text} is not valid.");

    internal static bool IsNumericSyntax(string text) => TryReadParts(text, out _);

    internal static bool TryParse(string text, out object value)
    {
        value = null!;
        if (!TryReadParts(text, out var parts)) return false;
        try
        {
            var suffix = parts.Suffix;
            if (parts.Real)
            {
                const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
                if (suffix == Suffix.Decimal)
                {
                    if (!decimal.TryParse(parts.Digits, styles, CultureInfo.InvariantCulture, out var decimalValue)) return false;
                    value = decimalValue * parts.Multiplier;
                    return true;
                }
                if (!double.TryParse(parts.Digits, styles, CultureInfo.InvariantCulture, out var doubleValue)) return false;
                if (doubleValue == 0 && parts.Negative) doubleValue = -0.0;
                doubleValue *= parts.Multiplier;
                if (suffix == Suffix.None) { value = doubleValue; return true; }
                // Native AsBigInt rounds to the nearest even integer after applying the multiplier.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/Utils.cs#L43-L163
                return TryFitInteger(new BigInteger(Math.Round(doubleValue)), suffix, decimalNotation: true, out value);
            }

            BigInteger integer;
            if (parts.Radix == 16)
            {
                var digits = "0" + parts.Digits;
                if (!IsUnsigned(suffix))
                {
                    var width = suffix switch
                    {
                        Suffix.SignedByte => 2,
                        Suffix.Short => 4,
                        Suffix.Long => 16,
                        _ => digits.Length < 16 ? 8 : 16
                    };
                    if (digits.Length == width + 1) digits = digits[1..];
                }
                if (!BigInteger.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out integer)) return false;
                if (digits.Length == 16 && suffix is Suffix.None or Suffix.Unsigned)
                    suffix = suffix == Suffix.None ? Suffix.Long : Suffix.ULong;
            }
            else if (parts.Radix == 2)
            {
                integer = ParseBinary(parts.Digits, IsUnsigned(suffix));
                if (parts.Digits.Length == 64 && suffix is Suffix.None or Suffix.Unsigned)
                    suffix = suffix == Suffix.None ? Suffix.Long : Suffix.ULong;
            }
            else if (!BigInteger.TryParse(parts.Digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer))
                return false;

            integer *= parts.Multiplier;
            if (parts.Radix != 10 && parts.Negative) integer = -integer;
            return TryFitInteger(integer, suffix, parts.Radix == 10, out value);
        }
        catch (OverflowException) { return false; }
    }

    private static bool TryFitInteger(BigInteger integer, Suffix suffix, bool decimalNotation, out object value)
    {
        value = null!;
        switch (suffix)
        {
            case Suffix.SignedByte when integer >= sbyte.MinValue && integer <= sbyte.MaxValue: value = (sbyte)integer; break;
            case Suffix.Byte when integer >= byte.MinValue && integer <= byte.MaxValue: value = (byte)integer; break;
            case Suffix.Short when integer >= short.MinValue && integer <= short.MaxValue: value = (short)integer; break;
            case Suffix.UShort when integer >= ushort.MinValue && integer <= ushort.MaxValue: value = (ushort)integer; break;
            case Suffix.Long when integer >= long.MinValue && integer <= long.MaxValue: value = (long)integer; break;
            case Suffix.ULong when integer >= ulong.MinValue && integer <= ulong.MaxValue: value = (ulong)integer; break;
            case Suffix.Decimal when integer >= (BigInteger)decimal.MinValue && integer <= (BigInteger)decimal.MaxValue:
                value = (decimal)integer; break;
            case Suffix.BigInteger: value = integer; break;
            case Suffix.Unsigned:
                if (integer >= uint.MinValue && integer <= uint.MaxValue) value = (uint)integer;
                else if (integer >= ulong.MinValue && integer <= ulong.MaxValue) value = (ulong)integer;
                break;
            case Suffix.None:
                if (integer >= int.MinValue && integer <= int.MaxValue) value = (int)integer;
                else if (integer >= long.MinValue && integer <= long.MaxValue) value = (long)integer;
                else if (decimalNotation && integer >= (BigInteger)decimal.MinValue && integer <= (BigInteger)decimal.MaxValue)
                    value = (decimal)integer;
                else if (decimalNotation && integer >= (BigInteger)double.MinValue && integer <= (BigInteger)double.MaxValue)
                    value = (double)integer;
                break;
        }
        return value is not null;
    }

    // ParseBinary accepts sign bits at native fixed widths, plus complete bytes at 128 bits and above.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/Utils.cs#L175-L258
    private static BigInteger ParseBinary(string digits, bool unsigned)
    {
        var bytes = new byte[(digits.Length + 7) / 8];
        for (var i = 0; i < digits.Length; i++)
        {
            var bit = digits.Length - 1 - i;
            if (digits[i] == '1') bytes[bytes.Length - 1 - bit / 8] |= (byte)(1 << (bit % 8));
        }
        unsigned |= digits[0] == '0' || !(digits.Length is 8 or 16 or 32 or 64 or 96 || digits.Length >= 128);
        return new BigInteger(bytes, isUnsigned: unsigned, isBigEndian: true);
    }

    private static bool TryReadParts(string text, out Parts parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(text)) return false;
        var position = 0;
        var negative = IsDash(text[0]);
        if (negative || text[0] == '+') position++;
        if (position == text.Length) return false;
        var numberStart = position;
        var radix = 10;
        var real = false;
        if (position + 1 < text.Length && text[position] == '0' && text[position + 1] is 'x' or 'X' or 'b' or 'B')
        {
            radix = text[position + 1] is 'x' or 'X' ? 16 : 2;
            position += 2;
            numberStart = position;
            while (position < text.Length && (radix == 16 ? IsHexDigit(text[position]) : text[position] is '0' or '1')) position++;
            if (radix == 2 && position == numberStart) return false;
        }
        else
        {
            while (position < text.Length && IsDecimalDigit(text[position])) position++;
            var leadingDigits = position - numberStart;
            if (position < text.Length && text[position] == '.')
            {
                real = true;
                position++;
                var fractionStart = position;
                while (position < text.Length && IsDecimalDigit(text[position])) position++;
                if (leadingDigits == 0 && position == fractionStart) return false;
            }
            else if (leadingDigits == 0) return false;
            if (position < text.Length && text[position] is 'e' or 'E')
            {
                real = true;
                position++;
                if (position < text.Length && (text[position] == '+' || IsDash(text[position]))) position++;
                var exponentStart = position;
                while (position < text.Length && IsDecimalDigit(text[position])) position++;
                if (position == exponentStart) return false;
            }
        }
        var numberEnd = position;
        var suffix = Suffix.None;
        if (position < text.Length)
        {
            suffix = char.ToLowerInvariant(text[position]) switch
            {
                'y' => Suffix.SignedByte, 's' => Suffix.Short, 'u' => Suffix.Unsigned,
                'l' => Suffix.Long, 'd' => Suffix.Decimal, 'n' => Suffix.BigInteger, _ => Suffix.None
            };
            if (suffix != Suffix.None) position++;
            if (suffix == Suffix.Unsigned && position < text.Length)
            {
                var combined = char.ToLowerInvariant(text[position]) switch
                {
                    'y' => Suffix.Byte, 's' => Suffix.UShort, 'l' => Suffix.ULong, _ => Suffix.Unsigned
                };
                if (combined != Suffix.Unsigned) { suffix = combined; position++; }
            }
        }
        var multiplier = 1L;
        if (position + 1 < text.Length && text[position + 1] is 'b' or 'B')
        {
            var shift = char.ToLowerInvariant(text[position]) switch { 'k' => 10, 'm' => 20, 'g' => 30, 't' => 40, 'p' => 50, _ => 0 };
            if (shift != 0) { multiplier = 1L << shift; position += 2; }
        }
        if (position != text.Length) return false;
        var digits = text[numberStart..numberEnd];
        if (radix == 10)
        {
            digits = digits.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2015', '-');
            if (negative) digits = "-" + digits;
        }
        parts = new(digits, radix, suffix, real, negative, multiplier);
        return true;
    }

    internal static bool IsDecimalDigit(char value) => value is >= '0' and <= '9';
    internal static bool IsHexDigit(char value) => IsDecimalDigit(value) || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    internal static bool IsDash(char value) => value is '-' or '\u2013' or '\u2014' or '\u2015';
    private static bool IsUnsigned(Suffix suffix) => suffix is Suffix.Byte or Suffix.UShort or Suffix.Unsigned or Suffix.ULong;
}
