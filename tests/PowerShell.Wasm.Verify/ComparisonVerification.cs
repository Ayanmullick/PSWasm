using PSWasm;

internal static class ComparisonVerification
{
    public static async ValueTask VerifyAsync()
    {
        await VerifyAsync("null values", """
$null -eq $null
$null -eq ''
'' -eq $null
$null -eq 0
0 -eq $null
$false -eq $null
$null -ne ''
$null -lt 0
0 -gt $null
$null -gt -1
-1 -lt $null
$null -le $null
$null -ge $null
$null -lt $null
$null -gt $null
""", ["True", "False", "False", "False", "False", "False", "True", "True", "True", "True",
            "True", "True", "True", "False", "False"]);

        await VerifyAsync("left-directed strings and numbers", """
'01' -eq 1
1 -eq '01'
'1.0' -eq 1
1 -eq '1.0'
'10' -lt 2
10 -lt '2'
'2' -gt 10
2 -gt '10'
1 -eq '1.1'
1 -eq '1.5'
1 -eq 1.1
[byte]255 -lt '256'
[long]1 -lt '1.5'
[decimal]'1.5' -eq '1.5'
0 -eq ''
1 -eq 'bad'
1 -ne 'bad'
try { 1 -lt 'bad' } catch { 'ordering-conversion-error' }
'Alpha' -eq 'alpha'
'Alpha' -ceq 'alpha'
""", ["False", "True", "False", "True", "True", "False", "True", "False", "False", "False",
            "False", "True", "True", "True", "True", "False", "True", "ordering-conversion-error", "True", "False"]);

        await VerifyAsync("left-directed booleans", """
$true -eq 'false'
$false -eq ''
$false -eq 'false'
$true -eq 2
2 -eq $true
1 -eq $true
$true -gt ''
$false -lt 'false'
$false -eq @()
$true -eq @(1, 2)
""", ["True", "True", "False", "True", "False", "True", "True", "True", "True", "True"]);

        await VerifyAsync("collection filtering and containment", """
(@($null, 0, '') -eq $null).Count
(@($null, 0, '') -ne $null).Count
(@('01', 1, '1') -eq 1) -join ','
(@('10', '2', '3') -lt 2) -join ','
@('01') -contains 1
@(1) -contains '01'
1 -in @('01')
'01' -in @(1)
@($null, '') -contains $null
@('') -contains $null
$null -contains $null
$null -in $null
@('Alpha') -contains 'alpha'
@('Alpha') -ccontains 'alpha'
@('bad') -notcontains 1
1 -notin @('bad')
""", ["1", "2", "1,1", "10", "False", "True", "False", "True", "True", "False", "True",
            "True", "True", "False", "True", "True"]);

        await VerifyAsync("string comparison with collection operands", """
$OFS = '|'
'1|2' -eq @(1, 2)
'1|2' -eq ([byte[]]@(1, 2))
'1|2' -ceq @(1, 2)
'1|2' -le @(1, 2)
'1|2' -ge @(1, 2)
""", ["True", "True", "True", "True", "True"]);

        await VerifyAsync("exact switch keeps textual matching", """
switch -Exact (1) {
    'bad' { 'wrong-invalid-text' }
    '01' { 'wrong-numeric-coercion' }
    '1' { 'one' }
}
switch -Exact ($true) {
    1 { 'wrong-boolean-coercion' }
    'true' { 'boolean' }
}
""", ["one", "boolean"]);
    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Comparison ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}
