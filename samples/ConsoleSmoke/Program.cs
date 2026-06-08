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
$count = 2 * (3 + 4)
Write-Output $count
$Out = @{InputObject= 'Splat works'}
Write-Output @Out
1 | Write-Output
$ReadParams = @{Endpoint= 'https://example.invalid/items'; Token= $env:DemoToken}
Read-ClientItems @ReadParams -PartitionKey 'demo-partition'
""";

var result = await runtime.ExecuteAsync(script);
Console.WriteLine(result.Text);
