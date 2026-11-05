using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PSWasm.Language;

// Adapted from PowerShell's WildcardPatternParser and WildcardPatternMatcher for browser-safe string matching.
// This is the language wildcard grammar, not filesystem/provider globbing. Match UTF-16 units, including newlines.
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/regex.cs#L632-L743
// https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/regex.cs#L956-L1029
internal static class PowerShellWasmWildcardPattern
{
    private enum ElementKind { Literal, Any, Star, Set }
    private readonly record struct Element(ElementKind Kind, char Literal = default, Regex? Set = null);

    internal static bool IsMatch(string value, string pattern, bool caseSensitive)
    {
        if (pattern == "*") return true;
        var culture = CultureInfo.CurrentCulture;
        var comparison = caseSensitive ? StringComparison.CurrentCulture :
            culture.Name.Equals("en-US-POSIX", StringComparison.OrdinalIgnoreCase)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.CurrentCultureIgnoreCase;
        var firstSpecial = pattern.AsSpan().IndexOfAny("*?[]`".AsSpan());
        // Preserve native literal/prefix comparisons as well as its character-based wildcard matcher.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/regex.cs#L140-L193
        if (firstSpecial < 0) return string.Equals(value, pattern, comparison);
        if (firstSpecial == pattern.Length - 1 && pattern[firstSpecial] == '*')
            return value.StartsWith(pattern[..firstSpecial], comparison);

        var elements = Parse(pattern, caseSensitive, culture);
        var current = new bool[elements.Count + 1];
        var next = new bool[elements.Count + 1];
        current[0] = true;
        AdvanceStars(elements, current);
        foreach (var character in value)
        {
            Array.Clear(next);
            var normalized = caseSensitive ? character : culture.TextInfo.ToLower(character);
            for (var index = 0; index < elements.Count; index++)
            {
                if (!current[index]) continue;
                var element = elements[index];
                if (element.Kind == ElementKind.Star) next[index] = true;
                else if (element.Kind == ElementKind.Any || element.Kind == ElementKind.Literal && element.Literal == normalized ||
                    element.Kind == ElementKind.Set && element.Set!.IsMatch(normalized.ToString())) next[index + 1] = true;
            }
            AdvanceStars(elements, next);
            (current, next) = (next, current);
        }
        return current[elements.Count];
    }

    private static void AdvanceStars(IReadOnlyList<Element> elements, bool[] positions)
    {
        for (var index = 0; index < elements.Count; index++)
            if (positions[index] && elements[index].Kind == ElementKind.Star) positions[index + 1] = true;
    }

    private static List<Element> Parse(string pattern, bool caseSensitive, CultureInfo culture)
    {
        var elements = new List<Element>();
        var characters = new List<(char Value, bool RangeOperator)>();
        var escaped = false;
        var inSet = false;
        var firstInSet = false;
        foreach (var character in pattern)
        {
            if (inSet)
            {
                if (character == ']' && !firstInSet && !escaped)
                {
                    elements.Add(new(ElementKind.Set, Set: CreateSet(characters, pattern, caseSensitive)));
                    inSet = false;
                }
                else if (character != '`' || escaped) characters.Add((character, character == '-' && !escaped));
                firstInSet = false;
            }
            else if (character == '*' && !escaped) elements.Add(new(ElementKind.Star));
            else if (character == '?' && !escaped) elements.Add(new(ElementKind.Any));
            else if (character == '[' && !escaped)
            {
                characters.Clear();
                inSet = true;
                firstInSet = true;
            }
            else if (character != '`' || escaped)
                elements.Add(new(ElementKind.Literal, caseSensitive ? character : culture.TextInfo.ToLower(character)));
            escaped = character == '`' && !escaped;
        }
        if (inSet) throw InvalidPattern(pattern);
        // Native compatibility: a lone backtick matches empty; a dangling backtick in a longer pattern is literal.
        if (escaped && pattern.Length != 1) elements.Add(new(ElementKind.Literal, '`'));
        return elements;
    }

    private static Regex CreateSet(IReadOnlyList<(char Value, bool RangeOperator)> characters, string pattern, bool caseSensitive)
    {
        var expression = new StringBuilder("[");
        for (var index = 0; index < characters.Count; index++)
        {
            var start = characters[index].Value;
            AppendCharacter(expression, start);
            if (index + 2 < characters.Count && characters[index + 1].RangeOperator)
            {
                var end = characters[index + 2].Value;
                if (start > end) throw InvalidPattern(pattern);
                expression.Append('-');
                AppendCharacter(expression, end);
                index += 2;
            }
        }
        expression.Append(']');
        // As upstream, use BCL character classes for culture-sensitive set/range matching; ! and ^ are literals.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/regex.cs#L1234-L1327
        try { return new Regex(expression.ToString(), caseSensitive ? RegexOptions.Singleline : RegexOptions.Singleline | RegexOptions.IgnoreCase); }
        catch (ArgumentException) { throw InvalidPattern(pattern); }
    }

    private static void AppendCharacter(StringBuilder expression, char character) =>
        expression.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));

    private static InvalidOperationException InvalidPattern(string pattern) =>
        new($"The specified wildcard character pattern is not valid: {pattern}");
}
