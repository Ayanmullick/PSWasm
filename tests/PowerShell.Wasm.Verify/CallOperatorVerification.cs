using PSWasm;

internal static class CallOperatorVerification
{
    public static async ValueTask VerifyAsync()
    {
        await VerifyAsync("named parameters aliases and prefixes", """
& { param($Name, $Value) "$Name|$Value|bound=$($PSBoundParameters.Count)" } -Value 7 -Name Ada
$block = {
    param([Alias('n')]$Name, $Value)
    "$Name|$Value|canonical=$($PSBoundParameters['Name'])|bound=$($PSBoundParameters.Count)"
}
& $block -n Ada 7
& $block 8 -Na Grace
& $block -Val 9 -Name Lin
try { & $block -Name Ada -n Other } catch { 'duplicate-alias' }
$ambiguous = { param($Name, $Number) 'should-not-run' }
try { & $ambiguous -N value } catch { 'ambiguous-prefix' }
""", ["Ada|7|bound=2", "Ada|7|canonical=Ada|bound=2", "Grace|8|canonical=Grace|bound=2",
            "Lin|9|canonical=Lin|bound=2", "duplicate-alias", "ambiguous-prefix"]);

        await VerifyAsync("switches and positional arguments", """
$block = {
    param([switch]$Flag, $Name='default', $Value='fallback')
    "$Flag|$Name|$Value|args=$($args -join ',')|bound=$($PSBoundParameters.Count)"
}
& $block -Flag Ada 7
& $block Ada -Flag 7
& $block Ada 7 -Flag
& $block -Flag:$false Ada 7
& $block -Flag:$null Ada 7
& $block -Flag $false Ada
& $block
try { & $block -Flag:'false' } catch { 'invalid-switch-string' }
try { & $block -Flag:1 } catch { 'invalid-switch-number' }
try { & $block -Name -Flag } catch { 'missing-parameter-value' }
""", ["True|Ada|7|args=|bound=3", "True|Ada|7|args=|bound=3", "True|Ada|7|args=|bound=3",
            "False|Ada|7|args=|bound=3", "False|Ada|7|args=|bound=3", "True|False|Ada|args=|bound=3",
            "False|default|fallback|args=|bound=0", "invalid-switch-string", "invalid-switch-number", "missing-parameter-value"]);

        await VerifyAsync("defaults null and automatic variables", """
$defaults = { param($First=$Second, $Second='fallback') "$First|$Second|bound=$($PSBoundParameters.Count)" }
& $defaults -Second supplied
& $defaults
& { param([int]$Count, [string]$Text, [bool]$Enabled) "$Count|$($Text.Length)|$Enabled|bound=$($PSBoundParameters.Count)" }
$nullArgument = { param($Name='default') "null=$($null -eq $Name)|bound=$($PSBoundParameters.Count)|args=$($args -join ',')" }
& $nullArgument -Name:$null extra tail
& $nullArgument
$unknown = { param($Name, $Value) "$Name|$Value|args=$($args -join ',')|bound=$($PSBoundParameters.Count)" }
& $unknown -Unused 9 Ada
& $unknown -Name Ada extra -Unused 9 tail
& { $args -join ','; $PSBoundParameters.Count } -Flag Ada -Other:$false tail
""", ["supplied|supplied|bound=1", "|fallback|bound=0", "0|0|False|bound=0",
            "null=True|bound=1|args=extra,tail", "null=False|bound=0|args=", "Ada||args=-Unused,9|bound=1",
            "Ada|extra|args=-Unused,9,tail|bound=2", "-Flag,Ada,-Other:,False,tail", "0"]);

        await VerifyAsync("positional and hashtable splats", """
$block = {
    param([switch]$Flag, [Alias('n')]$Name='default', $Value='fallback')
    "$Flag|$Name|$Value|args=$($args -join ',')|bound=$($PSBoundParameters.Count)"
}
$positionals = @('Ada', 7, 'extra')
& $block @positionals -Flag
$named = @{Flag=$false; Name='hash'; Value=8}
& $block @named
& $block @named -Name explicit
& $block -n explicit @named
$first = @{Name='first'}
$second = @{Name='second'}
& $block @first @second -Na explicit
& $block -Name explicit @first @second
try { & $block @first @second } catch { 'duplicate-splat' }
$unknown = [ordered]@{Unused=9; Name='hash'; Other=$false}
& $block before @unknown after
""", ["True|Ada|7|args=extra|bound=3", "False|hash|8|args=|bound=3",
            "False|explicit|8|args=|bound=3", "False|explicit|8|args=|bound=3",
            "False|explicit|fallback|args=|bound=1", "False|explicit|fallback|args=|bound=1", "duplicate-splat",
            "False|hash|before|args=after,-Unused:,9,-Other:,False|bound=2"]);

        await VerifyAsync("argument evaluation before binding", """
$block = { param($Name, $Value) "$Name|$Value" }
$counter = 0
try { & $block -Name (++$counter) -Name (++$counter) (++$counter) }
catch { "duplicate-count=$counter" }
$named = @{Name='first'}
& $block @named ($named.Name='second')
$positionals = @('first')
& $block @positionals ($positionals[0]='second')
""", ["duplicate-count=3", "second|second", "second|second"]);

        await VerifyAsync("parenthesized target is evaluated once", """
$targets = @({ param($Name, $Value) "$Name|$Value" }, { 'wrong-target' })
$counter = 0
& ($targets[(++$counter) - 1]) -Name Ada -Value (++$counter)
"counter=$counter"
& ('Write-' + 'Output') 'computed-command'
""", ["Ada|2", "counter=2", "computed-command"]);

        await VerifyAsync("dynamic registered commands and functions", """
& 'Write-Output' 'literal-command'
$commandName = 'ConvertTo-Json'
& $commandName -Compress @{Name='Ada'}
function Show-Dynamic { param([switch]$Flag, [Alias('n')]$Name, $Value) "$Flag|$Name|$Value|bound=$($PSBoundParameters.Count)" }
$commandName = 'Show-Dynamic'
& $commandName -Flag:$false -n Ada 7
& 'Show-Dynamic' -Flag Grace 8
$named = @{Name='hash'; Value=9}
& $commandName @named -Name explicit
@('bb', 'a', 'ccc') | & 'Sort-Object' -Descending Length
""", ["literal-command", "{\"Name\":\"Ada\"}", "False|Ada|7|bound=3", "True|Grace|8|bound=3",
            "False|explicit|9|bound=2", "ccc", "bb", "a"]);

        await VerifyAsync("pipeline input and child scope", """
$Name = 'outer'
$Value = 'retained'
$block = {
    param($Name, $Scale=1)
    foreach ($item in $input) { "$Name=$($item * $Scale)" }
    $Name = 'inner'
    $Value = 'changed'
    $onlyInside = 'local'
    "$Name|$Value|$onlyInside|bound=$($PSBoundParameters.Count)"
}
1..3 | & $block -Name item -Scale 10
"$Name|$Value|localIsNull=$($null -eq $onlyInside)"
& { param($Name) & { param($Name) $Name } -Name child; $Name } -Name parent
""", ["item=10", "item=20", "item=30", "inner|changed|local|bound=2", "outer|retained|localIsNull=True",
            "child", "parent"]);

        await VerifyAsync("restore variables and functions after child scope", """
function Show-Scope { 'outer-function' }
$value = 'outer-value'
& {
    function Show-Scope { 'inner-function' }
    function Only-Inside { 'new-inner-function' }
    Show-Scope
    Only-Inside
    $value = 'inner-value'
    $value
}
Show-Scope
$value
try { Only-Inside } catch { 'inner-function-unavailable' }
try {
    & {
        function Show-Scope { 'throwing-function' }
        function Only-BeforeThrow { 'new-throwing-function' }
        $value = 'throwing-value'
        Show-Scope
        throw 'call-stopped'
    }
} catch { "caught=$($_.Exception.Message)" }
Show-Scope
$value
try { Only-BeforeThrow } catch { 'throwing-function-unavailable' }
""", ["inner-function", "new-inner-function", "inner-value", "outer-function", "outer-value", "inner-function-unavailable",
            "throwing-function", "caught=call-stopped", "outer-function", "outer-value", "throwing-function-unavailable"]);

        await VerifyAsync("forward all emitted streams", """
$block = {
    param($Name)
    Write-Output "output:$Name"
    Write-Warning "warning:$Name"
    Write-Information "information:$Name" -InformationAction Continue
    Write-Error "error:$Name"
    Write-Output 'after-error'
}
& $block -Name Ada
'after-call'
""", ["output:Ada", "[Warning] warning:Ada", "[Information] information:Ada", "[Error] error:Ada", "after-error", "after-call"]);

        // These targets must fail within the registry; the operator must not parse command text or invoke host tools.
        foreach (var script in new[] { "& $null", "& ''", "& 'Write-Output unexpected'", "& 'Get-Process'", "& './missing.ps1'" })
        {
            try
            {
                await new PowerShellWasmRuntime().ExecuteAsync(script);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            throw new InvalidOperationException($"Call operator unexpectedly accepted an invalid browser target: {script}");
        }
    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Call operator ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}
