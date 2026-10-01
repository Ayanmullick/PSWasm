using PSWasm;

internal static class ParameterBindingVerification
{
    public static async ValueTask VerifyAsync()
    {
        await VerifyAsync("switches and positional order", """
function Show-Binding {
    param([switch]$Flag, $Name='default', $Value='fallback')
    "$Flag|$Name|$Value|args=$($args -join ',')|bound=$($PSBoundParameters.Count)"
}
Show-Binding -Flag Ada 7
Show-Binding Ada -Flag 7
Show-Binding Ada 7 -Flag
Show-Binding -Flag:$false Ada 7
Show-Binding -Flag $false Ada
Show-Binding -Name Ada -Flag 7
Show-Binding
Show-Binding -Flag:$null
try { Show-Binding -Flag:'false' } catch { 'invalid-switch-string' }
try { Show-Binding -Flag:1 } catch { 'invalid-switch-number' }
""", ["True|Ada|7|args=|bound=3", "True|Ada|7|args=|bound=3", "True|Ada|7|args=|bound=3",
            "False|Ada|7|args=|bound=3", "True|False|Ada|args=|bound=3", "True|Ada|7|args=|bound=3",
            "False|default|fallback|args=|bound=0", "False|default|fallback|args=|bound=1",
            "invalid-switch-string", "invalid-switch-number"]);

        await VerifyAsync("aliases prefixes and bound parameters", """
function Show-Alias {
    param([Alias('n')]$Name, $Value)
    "$Name|$Value|bound=$($PSBoundParameters.Count)|canonical=$($PSBoundParameters['Name'])"
}
Show-Alias -n Ada 7
Show-Alias -Na Ada -Val 7
Show-Alias 7 -Name Ada
$named = @{Na='hash'; Val=8}
Show-Alias @named
try { Show-Alias -Name Ada -n Other } catch { 'duplicate-alias' }
try { Show-Alias -Name Ada -Name Other } catch { 'duplicate-name' }
function Ambiguous($Name, $Number) { 'should-not-run' }
try { Ambiguous -N value } catch { 'ambiguous-prefix' }
""", ["Ada|7|bound=2|canonical=Ada", "Ada|7|bound=2|canonical=Ada", "Ada|7|bound=2|canonical=Ada",
            "hash|8|bound=2|canonical=hash", "duplicate-alias", "duplicate-name", "ambiguous-prefix"]);

        await VerifyAsync("argument evaluation order", """
function Show-Order($Left, $Right) { "$Left,$Right" }
$counter = 0
Show-Order -Left (++$counter) (++$counter)
$counter
$counter = 0
Show-Order (++$counter) -Right (++$counter)
$counter
""", ["1,2", "2", "1,2", "2"]);

        await VerifyAsync("evaluate arguments before binding", """
function Show-Evaluation($Name, $Value) { "$Name|$Value" }
$counter = 0
try { Show-Evaluation -Name (++$counter) -Name (++$counter) (++$counter) }
catch { "duplicate-count=$counter" }
function Ambiguous-Evaluation($Name, $Number) { 'should-not-run' }
$counter = 0
try { Ambiguous-Evaluation -N (++$counter) (++$counter) }
catch { "ambiguous-count=$counter" }
$named = @{Name='first'}
Show-Evaluation @named ($named.Name='second')
$positionals = @('first')
Show-Evaluation @positionals ($positionals[0]='second')
""", ["duplicate-count=3", "ambiguous-count=2", "second|second", "second|second"]);

        await VerifyAsync("splat precedence and positional values", """
function Show-Splat {
    param([switch]$Flag, $Name='default', $Value='fallback')
    "$Flag|$Name|$Value|bound=$($PSBoundParameters.Count)"
}
$named = @{Flag=$false; Name='hash'}
Show-Splat @named -Name explicit
Show-Splat -Name explicit @named
$first = @{Name='first'}
$second = @{Name='second'}
try { Show-Splat @first @second } catch { 'duplicate-splat' }
$positionals = @($false, 'Ada')
Show-Splat @positionals
$positionals = @('-Name', 'Ada')
Show-Splat @positionals
function Forward-Bound {
    param([switch]$Flag, $Name, $Value)
    Show-Splat @PSBoundParameters
}
Forward-Bound -Flag:$false -Name forwarded -Value 9
""", ["False|explicit|fallback|bound=2", "False|explicit|fallback|bound=2", "duplicate-splat",
            "False|False|Ada|bound=2", "False|-Name|Ada|bound=2", "False|forwarded|9|bound=3"]);

        await VerifyAsync("explicit parameters override multiple splats", """
function Show-Override {
    param([Alias('n')]$Name, $Value)
    "$Name|$Value|bound=$($PSBoundParameters.Count)"
}
$first = @{Name='first'}
$second = @{Name='second'}
Show-Override @first @second -Name explicit
Show-Override -Name explicit @first @second
Show-Override @first -n explicit @second
Show-Override @first @second -Na explicit
""", ["explicit||bound=1", "explicit||bound=1", "explicit||bound=1", "explicit||bound=1"]);

        await VerifyAsync("missing values and defaults", """
function Show-Defaults {
    param($First=$Second, $Second='fallback')
    "$First/$Second/$($PSBoundParameters.Count)"
}
Show-Defaults -Second supplied
Show-Defaults
function Show-TypedDefaults {
    param([int]$Count, [string]$Text, [bool]$Enabled)
    "$Count|$($Text.Length)|$Enabled|$($null -eq $Text)|bound=$($PSBoundParameters.Count)"
}
Show-TypedDefaults
function Needs-Value { param([switch]$Flag, $Name); 'should-not-run' }
try { Needs-Value -Name -Flag } catch { 'missing-parameter-value' }
function Show-Null($Name) { "null=$($null -eq $Name)|bound=$($PSBoundParameters.Count)" }
Show-Null -Name:$null
""", ["supplied/supplied/1", "/fallback/0", "0|0|False|False|bound=0", "missing-parameter-value", "null=True|bound=1"]);

        await VerifyAsync("unknown simple function arguments", """
function Show-Unknown($Name, $Value) { "$Name|$Value|args=$($args -join ',')" }
Show-Unknown -Unused 9 Ada
Show-Unknown -Name Ada extra -Unused 9 tail
function Show-AllArgs { $args -join ','; $PSBoundParameters.Count }
Show-AllArgs -Flag Ada -Other:$false tail
$unknown = [ordered]@{Unused=9; Name='hash'; Other=$false}
Show-Unknown before @unknown after
$first = @{Unknown=1}
$second = @{Unknown=2}
Show-AllArgs @first @second
Show-AllArgs -Unknown A -Unknown B
""", ["Ada||args=-Unused,9", "Ada|extra|args=-Unused,9,tail", "-Flag,Ada,-Other:,False,tail", "0",
            "hash|before|args=after,-Unused:,9,-Other:,False", "-Unknown:,1,-Unknown:,2", "0",
            "-Unknown,A,-Unknown,B", "0"]);

        await VerifyAsync("built-in switches before positional values", """
$bindingName = 'ok'
Get-Variable -ValueOnly bindingName
ConvertTo-Json -Compress @{Name='A'}
@('bb', 'a', 'ccc') | Sort-Object -Descending Length
@('bb', 'a', 'ccc') | Sort-Object -Descending:$false Length
@(Select-String -CaseSensitive 'a' -InputObject 'A').Count
@(Select-String -CaseSensitive:$false 'a' -InputObject 'A').Count
(Select-String -AllMatches:$false 'a' -InputObject 'aa').Matches.Count
(Select-String -AllMatches 'a' -InputObject 'aa').Matches.Count
@(Select-String -NotMatch:$false 'a' -InputObject 'a').Count
$null -eq (@(1, 2) | Measure-Object -Sum:$false).Sum
$null -eq (@(1, 2) | Measure-Object -Average:$false).Average
$null -eq (@(1, 2) | Measure-Object -Minimum:$false).Minimum
$null -eq (@(1, 2) | Measure-Object -Maximum:$false).Maximum
(@('bb', 'a') | Measure-Object -Sum Length).Sum
""", ["ok", "{\"Name\":\"A\"}", "ccc", "bb", "a", "a", "bb", "ccc", "0", "1", "1", "2", "1",
            "True", "True", "True", "True", "3"]);
    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Parameter binding ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}
