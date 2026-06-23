using System.Text.RegularExpressions;

namespace PSWasm.Commands;

internal sealed class WhereObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var scriptBlock = PowerShellWasmCommandUtilities.GetScriptBlock(context, "FilterScript", "Process");
        if (scriptBlock is not null)
        {
            foreach (var item in PowerShellWasmCommandUtilities.EnumeratePipelineInput(context.PipelineInput))
            {
                var filterOutput = await scriptBlock.InvokeAsync(item.Value, null, item.Variables, cancellationToken);
                if (filterOutput.Any(PowerShellWasmCommandUtilities.ToBoolean))
                {
                    context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(item.Value, item.Variables));
                }
            }

            return;
        }

        var propertyName = GetPropertyName(context);
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        var comparison = GetComparison(context);
        foreach (var item in PowerShellWasmCommandUtilities.EnumeratePipelineInput(context.PipelineInput))
        {
            var propertyValue = PowerShellWasmCommandUtilities.GetMemberValue(item.Value, propertyName);
            if (Matches(propertyValue, comparison))
            {
                context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(item.Value, item.Variables));
            }
        }
    }

    private static string? GetPropertyName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Property", out var property))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(property);
        }

        return context.Arguments
            .Where(static argument => argument is not PowerShellWasmScriptBlock)
            .Select(PowerShellWasmCommandUtilities.ToInvariantString)
            .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument));
    }

    private static WhereObjectComparison GetComparison(PowerShellWasmCommandContext context)
    {
        foreach (var candidate in s_comparisons)
        {
            if (context.Parameters.TryGetValue(candidate.ParameterName, out var expected))
            {
                return candidate with { Expected = expected };
            }
        }

        return new("Truthy", "Truthy", CaseSensitive: false, Expected: null);
    }

    private static bool Matches(object? value, WhereObjectComparison comparison) =>
        comparison.OperatorName switch
        {
            "Truthy" => PowerShellWasmCommandUtilities.ToBoolean(value),
            "EQ" => Compare(value, comparison.Expected, comparison.CaseSensitive) == 0,
            "NE" => Compare(value, comparison.Expected, comparison.CaseSensitive) != 0,
            "GE" => Compare(value, comparison.Expected, comparison.CaseSensitive) >= 0,
            "GT" => Compare(value, comparison.Expected, comparison.CaseSensitive) > 0,
            "LT" => Compare(value, comparison.Expected, comparison.CaseSensitive) < 0,
            "LE" => Compare(value, comparison.Expected, comparison.CaseSensitive) <= 0,
            "Like" => WildcardMatch(value, comparison.Expected, comparison.CaseSensitive),
            "NotLike" => !WildcardMatch(value, comparison.Expected, comparison.CaseSensitive),
            "Match" => Regex.IsMatch(
                PowerShellWasmCommandUtilities.ToInvariantString(value),
                PowerShellWasmCommandUtilities.ToInvariantString(comparison.Expected),
                RegexOptionsFor(comparison.CaseSensitive)),
            "NotMatch" => !Regex.IsMatch(
                PowerShellWasmCommandUtilities.ToInvariantString(value),
                PowerShellWasmCommandUtilities.ToInvariantString(comparison.Expected),
                RegexOptionsFor(comparison.CaseSensitive)),
            "Contains" => Contains(value, comparison.Expected, comparison.CaseSensitive),
            "NotContains" => !Contains(value, comparison.Expected, comparison.CaseSensitive),
            "In" => Contains(comparison.Expected, value, comparison.CaseSensitive),
            "NotIn" => !Contains(comparison.Expected, value, comparison.CaseSensitive),
            _ => false
        };

    private static int Compare(object? left, object? right, bool caseSensitive)
    {
        if (!caseSensitive)
        {
            return PowerShellWasmCommandUtilities.CompareValues(left, right);
        }

        return PowerShellWasmCommandUtilities.TryToNumber(left, out var leftNumber) &&
            PowerShellWasmCommandUtilities.TryToNumber(right, out var rightNumber)
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(
                    PowerShellWasmCommandUtilities.ToInvariantString(left),
                    PowerShellWasmCommandUtilities.ToInvariantString(right),
                    StringComparison.Ordinal);
    }

    private static bool WildcardMatch(object? value, object? pattern, bool caseSensitive)
    {
        var regexPattern = "^" +
            Regex.Escape(PowerShellWasmCommandUtilities.ToInvariantString(pattern))
                .Replace(@"\*", ".*", StringComparison.Ordinal)
                .Replace(@"\?", ".", StringComparison.Ordinal) +
            "$";

        return Regex.IsMatch(PowerShellWasmCommandUtilities.ToInvariantString(value), regexPattern, RegexOptionsFor(caseSensitive));
    }

    private static bool Contains(object? collection, object? value, bool caseSensitive) =>
        Enumerate(collection).Any(item => Compare(item, value, caseSensitive) == 0);

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private static RegexOptions RegexOptionsFor(bool caseSensitive) =>
        caseSensitive ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly WhereObjectComparison[] s_comparisons =
    [
        new("EQ", "EQ", CaseSensitive: false, Expected: null),
        new("CEQ", "EQ", CaseSensitive: true, Expected: null),
        new("NE", "NE", CaseSensitive: false, Expected: null),
        new("CNE", "NE", CaseSensitive: true, Expected: null),
        new("GE", "GE", CaseSensitive: false, Expected: null),
        new("CGE", "GE", CaseSensitive: true, Expected: null),
        new("GT", "GT", CaseSensitive: false, Expected: null),
        new("CGT", "GT", CaseSensitive: true, Expected: null),
        new("LT", "LT", CaseSensitive: false, Expected: null),
        new("CLT", "LT", CaseSensitive: true, Expected: null),
        new("LE", "LE", CaseSensitive: false, Expected: null),
        new("CLE", "LE", CaseSensitive: true, Expected: null),
        new("Like", "Like", CaseSensitive: false, Expected: null),
        new("CLike", "Like", CaseSensitive: true, Expected: null),
        new("NotLike", "NotLike", CaseSensitive: false, Expected: null),
        new("CNotLike", "NotLike", CaseSensitive: true, Expected: null),
        new("Match", "Match", CaseSensitive: false, Expected: null),
        new("CMatch", "Match", CaseSensitive: true, Expected: null),
        new("NotMatch", "NotMatch", CaseSensitive: false, Expected: null),
        new("CNotMatch", "NotMatch", CaseSensitive: true, Expected: null),
        new("Contains", "Contains", CaseSensitive: false, Expected: null),
        new("CContains", "Contains", CaseSensitive: true, Expected: null),
        new("NotContains", "NotContains", CaseSensitive: false, Expected: null),
        new("CNotContains", "NotContains", CaseSensitive: true, Expected: null),
        new("In", "In", CaseSensitive: false, Expected: null),
        new("CIn", "In", CaseSensitive: true, Expected: null),
        new("NotIn", "NotIn", CaseSensitive: false, Expected: null),
        new("CNotIn", "NotIn", CaseSensitive: true, Expected: null)
    ];

    private sealed record WhereObjectComparison(string ParameterName, string OperatorName, bool CaseSensitive, object? Expected);
}
