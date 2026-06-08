# PSWasm

PSWasm is a proof-of-concept browser WebAssembly runtime for a small PowerShell-compatible command surface.

The goal is to execute PowerShell text in a static web page without a build-time PowerShell-to-C# generation step. This repo starts with the narrow browser-safe pieces needed for the first package shape.

## Current Scope

The first runtime supports:

* `Write-Output`
* variable assignment
* hashtable literals
* splatting with `@Params`
* `$env:Name` lookup through a browser-provided environment map
* simple named parameters
* a pluggable command registry

The runtime intentionally does not include the full PowerShell host, providers, native command execution, remoting, jobs, module autoloading, profiles, formatting data, help, or OS-specific APIs.

## Layout

```text
src/PowerShell.Wasm
  Small PowerShell-compatible runtime and command registry.

samples/BrowserHost
  Static browser-wasm host that reads <script type="pwsh"> blocks at runtime.

samples/ConsoleSmoke
  Local smoke test for the runtime without the WebAssembly workload.
```

## Build

Install a .NET 10 SDK and, for the browser sample, the WebAssembly workload:

```powershell
dotnet workload install wasm-tools
```

Build the core runtime:

```powershell
dotnet build .\src\PowerShell.Wasm\PowerShell.Wasm.csproj
```

Run the local smoke sample:

```powershell
dotnet run --project .\samples\ConsoleSmoke\ConsoleSmoke.csproj
```

Publish the browser sample:

```powershell
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o publish /p:UseAppHost=false
```

The static files are emitted under:

```text
publish/wwwroot
```

## Browser POC

The browser sample reads inline PowerShell blocks like this at runtime:

```html
<script type="pwsh">
Write-Output 'Hello PowerShell'
</script>
```

Application-specific commands are registered by the host:

```csharp
var runtime = new PowerShellWasmRuntime(environment);
runtime.RegisterCommand("Read-ClientItems", new DelegatePowerShellWasmCommand(async (context, cancellationToken) =>
{
    var endpoint = context.GetString("Endpoint");
    var token = context.GetString("Token");
    var partitionKey = context.GetString("PartitionKey");

    await Task.Yield();
    context.ExecutionContext.WriteOutput($"{endpoint} {partitionKey} {token?.Length ?? 0}");
}));
```

For browser POCs, environment values can be passed from JavaScript:

```javascript
window.pswasmEnvironment = {
  DemoToken: "..."
};
```

Do not treat this as a production secret boundary. Any value sent to a static browser app is visible to the browser user.

## GitHub Pages

This repo deploys the browser host with GitHub Actions. After the workflow succeeds, the static loader is available at:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

## Direction

This is the first carve-out toward a browser-compatible PowerShell profile. The long-term path is to replace more of the small interpreter with reusable pieces from `System.Management.Automation` where they can be made `browser-wasm` compatible without pulling in the full desktop/server runtime.
