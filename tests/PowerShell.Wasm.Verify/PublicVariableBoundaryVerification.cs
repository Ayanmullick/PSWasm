using System.Text.Json;
using PSWasm;

internal static class PublicVariableBoundaryVerification
{
    public static async ValueTask VerifyAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        PowerShellWasmExecutionContext? capturedContext = null;
        IReadOnlyDictionary<string, object?>? snapshot = null;
        runtime.RegisterCommand("Capture-Context", new DelegatePowerShellWasmCommand((command, _) =>
        {
            capturedContext = command.ExecutionContext;
            snapshot = capturedContext.GetVariables();
            if (capturedContext.GetVariable("empty") is not null || capturedContext.GetVariable("actualNull") is not null ||
                capturedContext.GetVariable("missing") is not null || snapshot["EMPTY"] is not null || snapshot["actualNull"] is not null)
            {
                throw new InvalidOperationException("Public variable access must expose null, not the internal no-output marker.");
            }

            if (!ReferenceEquals(capturedContext.GetVariable("item"), snapshot["item"]) ||
                !ReferenceEquals(capturedContext.GetVariable("items"), snapshot["items"]))
            {
                throw new InvalidOperationException("Public variable access must preserve existing collection identity.");
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["empty"] = snapshot["empty"], ["actualNull"] = snapshot["actualNull"]
            });
            if (json != "{\"empty\":null,\"actualNull\":null}")
            {
                throw new InvalidOperationException($"Public null-valued variables must serialize as JSON null: {json}");
            }

            return ValueTask.CompletedTask;
        }));

        var result = await runtime.ExecuteAsync("""
$empty=& {}; $actualNull=$null; $item=@{Value=1}; $items=@(1,2)
Capture-Context
@($empty).Count
@($actualNull).Count
@((Get-Variable empty).Value).Count
@(Get-Variable empty -ValueOnly).Count
""");
        if (capturedContext is null || snapshot is null ||
            !result.Records.Select(static record => (record.Stream, record.Text)).SequenceEqual(
                [("Output", "0"), ("Output", "1"), ("Output", "0"), ("Output", "0")]))
        {
            throw new InvalidOperationException($"Public getters must not change the internal no-output state: {result.Text}");
        }

        capturedContext.SetVariable("later", 1);
        if (snapshot.ContainsKey("later"))
        {
            throw new InvalidOperationException("GetVariables must return a snapshot of the variable table.");
        }

        // Preserve the browser host's mapping display while distinguishing custom-object and hashtable type identity.
        var mapping = await new PowerShellWasmRuntime().ExecuteAsync("@{A=1}");
        var customObject = await new PowerShellWasmRuntime().ExecuteAsync("[pscustomobject]@{A=1}");
        if (!mapping.Records.SequenceEqual(customObject.Records))
        {
            throw new InvalidOperationException("Distinct custom-object identity must not change its existing browser display.");
        }
    }
}
