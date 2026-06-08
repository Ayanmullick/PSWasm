using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PSWasm;

namespace PSWasm.BrowserHost;

public static partial class Interop
{
    [SupportedOSPlatform("browser")]
    [JSExport]
    public static async Task<string> ExecuteAsync(string script, string environmentJson)
    {
        var environment = BrowserEnvironment.Parse(environmentJson);
        var runtime = new PowerShellWasmRuntime(environment);

        runtime.RegisterCommand("Read-ClientItems", new DelegatePowerShellWasmCommand((context, cancellationToken) =>
        {
            var endpoint = context.GetString("Endpoint");
            var token = context.GetString("Token");
            var partitionKey = context.GetString("PartitionKey");

            context.ExecutionContext.WriteOutput($"Host command: endpoint={endpoint}; partitionKey={partitionKey}; tokenLength={token?.Length ?? 0}");
            return ValueTask.CompletedTask;
        }));

        var result = await runtime.ExecuteAsync(script);
        return result.Text;
    }

    public static void Main()
    {
        Console.WriteLine("PSWasm browser host ready.");
    }
}
