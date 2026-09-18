# PSWasm

PSWasm is a proof-of-concept WebAssembly runtime that runs browser-safe PowerShell-compatible scripts in static web pages. It does not require a build-time PowerShell-to-C# conversion step.

## Quick Start

Try the [hosted browser sample](https://ayanmullick.github.io/PSWasm/) or add the hosted loader to a page served over HTTP:

```html
<script type="pwsh">
$Name = 'PSWasm'
Write-Output "Hello $Name"
</script>

<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

PowerShell can also live in a same-origin `.ps1` file. The `web` flavor includes DOM interaction and browser HTTP commands:

```html
<script type="pwsh" src="./sample.ps1"></script>
<script type="module" src="https://ayanmullick.github.io/PSWasm/web/app.js"></script>
```

Inline and external scripts run in document order and share a runtime session. Browser fetch rules, including CORS, apply. For scripts that update the DOM directly, `data-pswasm-output="none"` hides the generated success-output box while preserving errors. See [Browser Usage](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Usage).

## Build and Run Locally

Prerequisites: Git, the .NET 10 SDK selected by [global.json](global.json), the `wasm-tools` workload, and the `dotnet-serve` tool for the local server. PowerShell 7 is required for the repository's `.ps1` tooling. See [setup and validation details](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation#prerequisites).

Clone the repository, then publish and serve the browser sample:

```powershell
git clone https://github.com/Ayanmullick/PSWasm.git
cd PSWasm
dotnet publish ./samples/BrowserHost/PSWasm.BrowserHost.csproj -c Release -r browser-wasm `
  -o ./.WorkDir/build/publish/BrowserHost /p:UseAppHost=false
dotnet serve --directory ./.WorkDir/build/publish/BrowserHost/wwwroot --port 5010 --address 127.0.0.1
```

Open `http://127.0.0.1:5010/`. Publishing restores dependencies and builds the sample; add `--no-restore` only when matching restore assets already exist. Stop the server with Ctrl+C. Generated build output stays under `.WorkDir/build/`.

To build only the runtime, without publishing the browser sample:

```powershell
dotnet build ./src/PowerShell.Wasm/PowerShell.Wasm.csproj -c Release
```

## Package for a Static App

Use the hosted loader above, or publish files to serve with your app:

```powershell
pwsh -NoProfile -File ./tools/Publish-BrowserFlavors.ps1 -Flavor web
```

Copy `app.js`, `app.d.ts`, and `_framework/**` from `.WorkDir/build/publish/BrowserFlavors/web/wwwroot/` together. The public tooling has two entry points:

* [Publish-BrowserFlavors.ps1](tools/Publish-BrowserFlavors.ps1) publishes `core`, `web`, `AzAuth`, or `full` packages, with optional versioned hosting folders.
* [Measure-BrowserPayload.ps1](tools/Measure-BrowserPayload.ps1) reports raw and compressed payload sizes.

The `AzAuth` flavor adds user-delegated Entra authentication and authenticated Azure REST calls. See [Runtime Flavors and Payload Optimization](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Flavors-and-Payload-Optimization) and [Azure Auth Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/Azure-Auth-Cmdlets).

## Supported Scope

The runtime includes PowerShell-style variables, arrays, hashtables, objects, operators, functions, control flow, splatting, pipelines, stream output, and a limited set of .NET helpers. Browser commands cover JSON/CSV/HTML conversion, DOM events and storage, HTTP requests, and optional Azure authentication.

PSWasm does not provide the full desktop/server PowerShell host. Providers, native processes, profiles, remoting, jobs, unrestricted filesystem access, module autoloading, and arbitrary .NET reflection are outside its browser-safe scope.

## Documentation and Contributing

* [Runtime Scope](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Scope) and [Language Support](https://github.com/Ayanmullick/PSWasm/wiki/Language-Support)
* [Browser Commands](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Commands)
* [DOM Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/DOM-Cmdlets) and [DOM Cmdlet Reference](https://github.com/Ayanmullick/PSWasm/wiki/DOM-Cmdlet-Reference)
* [Browser Usage](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Usage)
* [Azure Auth Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/Azure-Auth-Cmdlets) and [Invoke-AzRestMethod](https://github.com/Ayanmullick/PSWasm/wiki/Invoke-AzRestMethod)
* [Build and Validation](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation): prerequisites, maintainer checks, output cleanup, and GitHub Pages deployment
* [Browser DOM Smoke](tests/BrowserDomSmoke/README.md): run browser assertions and submit their JSON report
* [Architecture and Direction](https://github.com/Ayanmullick/PSWasm/wiki/Architecture-and-Direction)

Runtime verification, browser packaging, and DOM smoke checks are documented separately from the quick start.

## License

PSWasm is available under the [MIT License](https://choosealicense.com/licenses/mit/).
