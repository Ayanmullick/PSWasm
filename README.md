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
* [Build and Validation](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation)
* [Architecture and Direction](https://github.com/Ayanmullick/PSWasm/wiki/Architecture-and-Direction)

## Current Highlights

PSWasm currently includes:

* browser-safe parser, AST profile, executor, session state, command dispatch, and object pipeline support
* variables, parallel assignment, arrays, hashtables, splatting, expandable strings, script blocks, functions, loops, `switch`, `try` / `catch` / `finally`, `throw`, `return`, `break`, and `continue`
* common PowerShell-style operators, including arithmetic, comparisons, wildcard/regex operators, `-replace`, `-split`, `-join`, `-f`, `&&`, `||`, `$i++`, `$i--`, and `??`
* stream-aware `Write-*` commands, browser-safe variable commands, JSON/CSV/object pipeline commands, `Invoke-WebRequest`, and DOM session/interaction commands
* allowlisted browser-safe .NET helpers for Base64, UTF-8 bytes, HMACSHA256, URI escaping/unescaping, and simple string methods
* a browser host that auto-runs `<script type="pwsh">` blocks and exposes JavaScript helpers for custom hosts

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

## Maintainer Checks

Run assertion-based runtime verification:

```powershell
dotnet run --project .\tests\PowerShell.Wasm.Verify\PowerShell.Wasm.Verify.csproj --configuration Release
```

Publish-check the browser host:

```powershell
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o .\artifacts\BrowserHost /p:UseAppHost=false --no-restore
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
