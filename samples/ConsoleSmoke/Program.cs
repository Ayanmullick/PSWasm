using PSWasm;

var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["DemoPrefix"] = "local"
};

var runtime = new PowerShellWasmRuntime(environment);
runtime.RegisterCommand("Invoke-HostEcho", new DelegatePowerShellWasmCommand((context, cancellationToken) =>
{
    var message = context.GetString("Message");
    var prefix = context.GetString("Prefix");

    context.ExecutionContext.WriteOutput($"Host command: {prefix}: {message}");
    return ValueTask.CompletedTask;
}));

var script = """
Write-Output 'Hello PowerShell'
Write-Debug 'Debug message'
Write-Error 'Error message'
Write-Host 'Host message'
Write-Information 'Information message'
Write-Progress -Activity 'Loading' -Status 'Halfway' -PercentComplete 50
Write-Verbose 'Verbose message'
Write-Warning 'Warning message'
Get-Date -Format 'yyyy-MM-dd'
Get-Time
Get-TimeZone
$name = 'PowerShell'
Write-Output "Hello $name"
Write-Output (2+2)
2 + 2
($var = 1 + 2) -eq 3
$var
$count = 2 * (3 + 4)
Write-Output $count
5 % 2
1..3
-not $false
-bnot 0
$true -and (2 -eq 2)
$false -or $true
2 -band 3
1 -shl 3
'hello' -like 'he*'
'hello' -cmatch 'H'
'abc' -replace @('b','x')
@('red','blue') -contains 'blue'
'blue' -in @('red','blue')
@('a','b') -join '-'
'a b c' -split ' '
'{0}-{1}' -f @('left','right')
$missing ?? 'fallback'
$Out = @{InputObject= 'Splat works'}
Write-Output @Out
1 | Write-Output
1..4 | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 10 }
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Where-Object { $PSItem.Value -ge 2 } |
    Select-Object -ExpandProperty Name
@{Name='browser'; Value=42} | ConvertTo-Json -Compress
'{"Name":"json","Value":7}' | ConvertFrom-Json | Select-Object -ExpandProperty Name
@(@{Name='three'; Value=3}, @{Name='one'; Value=1}, @{Name='two'; Value=2}) |
    Sort-Object Value |
    Select-Object -ExpandProperty Name
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Measure-Object Value -Sum -Average -Minimum -Maximum |
    ConvertTo-Json -Compress
@('b','a','b') |
    Group-Object |
    Sort-Object Name |
    Select-Object Count Name |
    ConvertTo-Json -Compress
@{Name='one'; Value=1} | Out-String
$EchoParams = @{Message= 'from host'; Prefix= $env:DemoPrefix}
Invoke-HostEcho @EchoParams
""";

var result = await runtime.ExecuteAsync(script);
Console.WriteLine(result.Text);
