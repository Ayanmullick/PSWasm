# PSWasm

PSWasm is a proof-of-concept browser WebAssembly runtime for a browser-safe PowerShell-compatible command surface.

The goal is to execute PowerShell text in static web pages without a build-time PowerShell-to-C# generation step. The runtime focuses on useful PowerShell syntax, variables, operators, hashtables, splatting, pipelines, stream output, browser-safe built-in commands, and an importable static-browser package.

## Quick Start

Use the hosted browser loader in a static page:

```html
<script type="pwsh">
$Name = 'PSWasm'
Write-Output "Hello $Name"
</script>

<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

Use a hosted trimmed flavor when the page only needs a specific browser-safe feature set:

```html
<script type="pwsh" src="./sample.ps1"></script>
<script type="module" src="https://ayanmullick.github.io/PSWasm/dom-web-crypto/app.js"></script>
```

PowerShell can also live in a same-origin `.ps1` file:

```html
<script type="pwsh" src="./sample.ps1"></script>
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js?v=latest"></script>
```

External and inline `type="pwsh"` scripts run in document order and share the same default runtime session. Normal browser fetch rules apply to external `.ps1` files, including CORS for cross-origin files.

## Documentation

The detailed documentation lives in the GitHub Wiki:

* [Runtime Scope](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Scope)
* [Language Support](https://github.com/Ayanmullick/PSWasm/wiki/Language-Support)
* [Browser Commands](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Commands)
* [DOM Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/DOM-Cmdlets)
* [Browser Usage](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Usage)
* [Runtime Flavors and Payload Optimization](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Flavors-and-Payload-Optimization)
* [Build and Validation](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation)
* [Architecture and Direction](https://github.com/Ayanmullick/PSWasm/wiki/Architecture-and-Direction)

## Current Highlights

The default/full PSWasm browser host currently includes:

* browser-safe parser, AST profile, executor, session state, command dispatch, and object pipeline support
* variables, parallel assignment, arrays, hashtables, splatting, expandable strings, script blocks, functions, loops, `switch`, `try` / `catch` / `finally`, `throw`, `return`, `break`, and `continue`
* common PowerShell-style operators, including arithmetic, comparisons, wildcard/regex operators, `-replace`, `-split`, `-join`, `-f`, `&&`, `||`, `$i++`, `$i--`, and `??`
* stream-aware `Write-*` commands, browser-safe variable commands, JSON/CSV/object pipeline commands, `Invoke-WebRequest`, and DOM session/interaction commands
* allowlisted browser-safe .NET helpers for Base64, UTF-8 bytes, HMACSHA256, URI escaping/unescaping, and simple string methods
* a browser host that auto-runs `<script type="pwsh">` blocks and exposes JavaScript helpers for custom hosts

Trimmed browser flavors can omit optional DOM, web request, or crypto/text helper groups. See [Runtime Flavors and Payload Optimization](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Flavors-and-Payload-Optimization).

PSWasm intentionally does not expose the full desktop/server PowerShell host. Providers, native process execution, profiles, remoting, jobs, unrestricted filesystem access, module autoloading, and arbitrary .NET reflection are outside the browser-safe scope.

## Build

Install a .NET 10 SDK and, for the browser sample, the WebAssembly workload:

```powershell
dotnet workload install wasm-tools
```

Build the core runtime:

```powershell
dotnet build .\src\PowerShell.Wasm\PowerShell.Wasm.csproj
```

Publish the browser host:

```powershell
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o publish /p:UseAppHost=false
```

The static files are emitted under:

```text
publish/wwwroot
```

Publish clean browser flavors for payload comparison:

```powershell
.\tools\Publish-BrowserFlavors.ps1 -Flavor core,dom,crypto,web,dom-web-crypto
```

Use `dom-web-crypto` for static pages that need DOM event binding, `Invoke-WebRequest`, and browser-safe HMAC/Base64/URI helper coverage.
Flavor output is package-shaped by default: copy `app.js`, `app.d.ts`, and `_framework/**` from `artifacts/BrowserFlavors/<flavor>/wwwroot` into your static app.

Publish host-ready flavor folders for static hosting:

```powershell
.\tools\Publish-BrowserFlavors.ps1 -Flavor core,dom-web-crypto -HostedRoot .\artifacts\HostedBrowserFlavors -HostedVersion v0.1.0
```

That creates `artifacts/HostedBrowserFlavors/<flavor>/app.js` and `artifacts/HostedBrowserFlavors/v0.1.0/<flavor>/app.js`, with each `app.js` loading its own sibling `_framework/**` folder.

## Maintainer Checks

Run assertion-based runtime verification:

```powershell
dotnet run --project .\tests\PowerShell.Wasm.Verify\PowerShell.Wasm.Verify.csproj --configuration Release
```

Publish-check the browser host:

```powershell
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o .\artifacts\BrowserHost /p:UseAppHost=false --no-restore
```

Measure a published browser payload:

```powershell
.\tools\Measure-BrowserPayload.ps1 -Path .\artifacts\BrowserHost\wwwroot -SummaryOnly
```

Run package-shape and flavor-gating smoke checks:

```powershell
.\tests\BrowserFlavorSmoke\Invoke-BrowserFlavorSmoke.ps1 -NoRestore
```

Run the real browser DOM smoke test after DOM command or browser DOM bridge changes:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1
```

If headless Edge or Chrome cannot be launched in the current environment, run the manual smoke server and open the printed URL in Microsoft Edge Tools for VS Code or the VS Code integrated browser:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1 -Manual
```

## GitHub Pages

This repo deploys the browser host with GitHub Actions. After the workflow succeeds, the static loader is available at:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

The Pages artifact also publishes hosted flavor folders:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/dom-web-crypto/app.js"></script>
```

Manual Pages workflow runs can provide a version folder, such as `v0.1.0`, for stable consumption:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/v0.1.0/dom-web-crypto/app.js"></script>
```
