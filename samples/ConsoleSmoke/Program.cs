using PSWasm;

var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["DemoToken"] = "local-demo-token"
};

var runtime = new PowerShellWasmRuntime(environment);
runtime.RegisterCommand("Read-ClientItems", new DelegatePowerShellWasmCommand((context, cancellationToken) =>
{
    var endpoint = context.GetString("Endpoint");
    var token = context.GetString("Token");
    var partitionKey = context.GetString("PartitionKey");

    context.ExecutionContext.WriteOutput($"Host command: endpoint={endpoint}; partitionKey={partitionKey}; tokenLength={token?.Length ?? 0}");
    return ValueTask.CompletedTask;
}));

var script = """
Write-Output 'Hello PowerShell'
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
$ReadParams = @{Endpoint= 'https://example.invalid/items'; Token= $env:DemoToken}
Read-ClientItems @ReadParams -PartitionKey 'demo-partition'
""";

var result = await runtime.ExecuteAsync(script);
Console.WriteLine(result.Text);
