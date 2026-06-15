using PSWasm;

var runtime = new PowerShellWasmRuntime();

await ExpectLinesAsync(runtime, """
Get-Command Invoke-WebRequest
Get-Command New-DomSession
Get-Command Register-DomEvent
'optional commands excluded'
""", [
    "optional commands excluded"
]);

await ExpectRuntimeErrorAsync(
    runtime,
    "Invoke-WebRequest -Uri 'https://example.test/'",
    "Command 'Invoke-WebRequest' is not registered in this browser runtime.");

await ExpectRuntimeErrorAsync(
    runtime,
    "New-DomSession -Name Main -Target document",
    "Command 'New-DomSession' is not registered in this browser runtime.");

await ExpectRuntimeErrorAsync(
    runtime,
    "[Convert]::ToBase64String(@())",
    "Type literal [Convert] is not available in this browser-safe runtime.");

await ExpectLinesAsync(runtime, """
'MIXED'.ToLowerInvariant()
'  padded  '.Trim()
""", [
    "mixed",
    "padded"
]);

Console.WriteLine("PASS core flavor excludes optional DOM, web, and crypto bridge features");

static async ValueTask ExpectLinesAsync(PowerShellWasmRuntime runtime, string script, string[] expected)
{
    var result = await runtime.ExecuteAsync(script);
    var actual = result.Text.Split(Environment.NewLine, StringSplitOptions.None);
    if (!actual.SequenceEqual(expected))
    {
        Fail($"Expected lines:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
    }
}

static async ValueTask ExpectRuntimeErrorAsync(PowerShellWasmRuntime runtime, string script, string expectedMessage)
{
    try
    {
        await runtime.ExecuteAsync(script);
    }
    catch (InvalidOperationException error) when (error.Message.Equals(expectedMessage, StringComparison.Ordinal))
    {
        return;
    }

    Fail($"Expected runtime error '{expectedMessage}'.");
}

static void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.ExitCode = 1;
    throw new InvalidOperationException(message);
}
