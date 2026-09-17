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
<script type="module" src="https://ayanmullick.github.io/PSWasm/web/app.js"></script>
```

Use the Azure-auth flavor when a static page needs browser-safe user-delegated Entra tokens:

```html
<script type="pwsh">
$TenantId,$ClientId = '<tenant-id>','<spa-client-id>'
Connect-AzAccount -Tenant $TenantId -ClientId $ClientId
$Response = Invoke-AzRestMethod -Path '/subscriptions/<sub-id>?api-version=2020-01-01'
</script>

<script type="module" src="https://ayanmullick.github.io/PSWasm/AzAuth/app.js"></script>
```

After the first sign-in, `Get-AzContext` and `Get-AzAccessToken` can recover the cached browser auth context. See [Azure Auth Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/Azure-Auth-Cmdlets).

PowerShell can also live in a same-origin `.ps1` file:

```html
<script type="pwsh" src="./sample.ps1"></script>
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js?v=latest"></script>
```

External and inline `type="pwsh"` scripts run in document order and share the same default runtime session. Normal browser fetch rules apply to external `.ps1` files, including CORS for cross-origin files.
For DOM-only script blocks that update existing page elements directly, add `data-pswasm-output="none"` to suppress the generated success output box while still showing runtime failures.

## Documentation

The detailed documentation lives in the GitHub Wiki:

* [Runtime Scope](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Scope)
* [Language Support](https://github.com/Ayanmullick/PSWasm/wiki/Language-Support)
* [Browser Commands](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Commands)
* [DOM Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/DOM-Cmdlets)
* [DOM Cmdlet Reference](https://github.com/Ayanmullick/PSWasm/wiki/DOM-Cmdlet-Reference)
* [Azure Auth Cmdlets](https://github.com/Ayanmullick/PSWasm/wiki/Azure-Auth-Cmdlets)
* [Invoke-AzRestMethod](https://github.com/Ayanmullick/PSWasm/wiki/Invoke-AzRestMethod)
* [Browser Usage](https://github.com/Ayanmullick/PSWasm/wiki/Browser-Usage)
* [Runtime Flavors and Payload Optimization](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Flavors-and-Payload-Optimization)
* [Build and Validation](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation)
* [Architecture and Direction](https://github.com/Ayanmullick/PSWasm/wiki/Architecture-and-Direction)

## Current Highlights

The default/full PSWasm browser host currently includes:

* browser-safe parser, AST profile, executor, session state, command dispatch, and object pipeline support
* variables, parallel assignment, arrays, `$()` / `@(...)` subexpressions with PowerShell-style output shaping and collection truthiness, hashtables with literal and computed keys, `[pscustomobject]@{}` / `[ordered]@{}` literals, quoted/computed member names, member/index assignment, compound assignment, and increment/decrement on mutable browser-safe values, collection member enumeration and intrinsic `.ForEach({})` / `.Where({})` methods, primitive casts, splatting, expandable strings and here-strings with `$()` subexpressions, block comments, backtick and implicit line continuation, parenthesized command/script expressions, script blocks, simple typed/default `param(...)` blocks, functions, loops, `switch` wildcard/exact/regex matching, `try` / `catch` / `finally`, `throw`, `return`, `break`, and `continue`
* common PowerShell-style operators, including arithmetic, compound assignment, ternary `? :`, null-conditional member access `?.`, scalar and collection comparisons, collection-aware wildcard/regex/string operators, primitive type operators, script block call operator `&`, `!` / `-not`, `-replace`, `-split`, `-join`, `-f`, `&&`, `||`, `$i++`, `$i--`, `??`, and `??=`
* stream-aware `Write-*` commands, browser-safe variable commands, JSON/CSV/HTML/object pipeline commands including `Where-Object` property filters, `ForEach-Object` member-name calls, and `Select-Object` wildcard/calculated properties, `?` / `%` pipeline aliases, `Invoke-WebRequest`, `Invoke-RestMethod`, `Invoke-AzRestMethod`, DOM session/interaction/storage commands, and user-delegated browser Azure auth commands
* allowlisted browser-safe .NET helpers for Base64, UTF-8 bytes, HMACSHA256, URI escaping/unescaping, simple string methods, `[Math]`, date/time types, time zone metadata, `ArrayList`, and generic `List<T>` construction/casts
* a browser host that auto-runs `<script type="pwsh">` blocks and exposes JavaScript helpers for custom hosts

Browser flavors are limited to `core`, `web`, `AzAuth`, and `full`. See [Runtime Flavors and Payload Optimization](https://github.com/Ayanmullick/PSWasm/wiki/Runtime-Flavors-and-Payload-Optimization).

PSWasm intentionally does not expose the full desktop/server PowerShell host. Providers, native process execution, profiles, remoting, jobs, unrestricted filesystem access, module autoloading, and arbitrary .NET reflection are outside the browser-safe scope.

## Build

Install a .NET 10 SDK and, for the browser sample, the WebAssembly workload:

```powershell
dotnet workload install wasm-tools
```

The build commands below assume dependencies are already restored. A fresh checkout or changed intermediate-output path requires an approved restore before using `--no-restore` or `-NoRestore`. Each feature combination has its own intermediate directory: restore `core`, `web`, and `full`/`AzAuth` with their matching feature flags before publishing those flavors without restore.

Build the core runtime:

```powershell
.\tools\Invoke-WorkspaceCommand.ps1 dotnet build .\src\PowerShell.Wasm\PowerShell.Wasm.csproj --no-restore
```

Publish the browser host:

```powershell
.\tools\Invoke-WorkspaceCommand.ps1 dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj `
  -c Release -r browser-wasm -o .\.WorkDir\build\publish\BrowserHost /p:UseAppHost=false --no-restore
```

The static files are emitted under:

```text
.WorkDir/build/publish/BrowserHost/wwwroot
```

Build output, publish output, test results, and temporary files used by the workspace scripts live under `.WorkDir/`. Run direct tool commands through `tools/Invoke-WorkspaceCommand.ps1` to keep their temporary files there for the command's lifetime.

`.WorkDir/checkouts/` holds local Git checkouts, including `PSWasm.wiki`, and must be preserved. Clean only the generated output that needs rebuilding; never delete `.WorkDir/` as a whole. See [Build and Validation](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation) for the workspace layout and validation workflow.

The workspace scripts preflight protected paths, allowing documented Windows Cloud Files tags while rejecting redirects, unsupported tags, and metadata-query errors. Keep all workspace files locally available before running these tools; availability is a user-managed prerequisite and is not checked automatically. Build and browser validation is still required after moving a checkout. See [Workspace path safety](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation#workspace-path-safety) for scope and limitations.

Publish clean browser flavors for payload comparison:

```powershell
.\tools\Publish-BrowserFlavors.ps1 -Flavor core,web,AzAuth,full -NoRestore
```

Use `web` for static pages that need DOM event binding, `Invoke-WebRequest`, and `Invoke-RestMethod`.
Use `AzAuth` for static pages that need DOM event binding, browser HTTP commands, browser-safe HMAC/Base64/URI helper coverage, user-delegated Entra access tokens, and authenticated Azure REST calls.
Flavor output is package-shaped by default: copy `app.js`, `app.d.ts`, and `_framework/**` from `.WorkDir/build/publish/BrowserFlavors/<flavor>/wwwroot` into your static app.

For browser DOM output, generate HTML as PowerShell output and render it explicitly:

```powershell
$Rows | ConvertTo-Html -Fragment -Property Id,Name,Status | Set-DomHtml '#output'
```

Publish host-ready flavor folders for static hosting:

```powershell
.\tools\Publish-BrowserFlavors.ps1 -Flavor core,web,AzAuth,full `
  -HostedRoot .\.WorkDir\build\publish\BrowserFlavorHosted -HostedVersion v0.1.0 -NoRestore
```

That creates `.WorkDir/build/publish/BrowserFlavorHosted/<flavor>/app.js` and `.WorkDir/build/publish/BrowserFlavorHosted/v0.1.0/<flavor>/app.js`, with each `app.js` loading its own sibling `_framework/**` folder.

## Maintainer Checks

Run assertion-based runtime verification:

```powershell
.\tools\Invoke-WorkspaceCommand.ps1 dotnet run --project .\tests\PowerShell.Wasm.Verify\PowerShell.Wasm.Verify.csproj `
  --configuration Release --no-restore
```

Publish-check the browser host:

```powershell
.\tools\Invoke-WorkspaceCommand.ps1 dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj `
  -c Release -r browser-wasm -o .\.WorkDir\build\publish\BrowserHost /p:UseAppHost=false --no-restore
```

Measure a published browser payload:

```powershell
.\tools\Measure-BrowserPayload.ps1 -Path .\.WorkDir\build\publish\BrowserHost\wwwroot -SummaryOnly
```

Run package-shape and flavor-gating smoke checks:

```powershell
.\tests\BrowserFlavorSmoke\Invoke-BrowserFlavorSmoke.ps1 -NoRestore
```

Run the browser DOM smoke test after DOM command or browser DOM bridge changes. It requires the VS Code `integrated_browser` MCP; check `browser_status` before starting:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1
```

The script starts an isolated local test site and waits for an MCP-connected agent to run the assertions and submit their JSON report. Run folders use short, readable UTC timestamps instead of GUIDs. An unavailable MCP blocks the check; there is no external-browser fallback. See [Browser DOM Smoke](tests/BrowserDomSmoke/README.md) for the complete run and report-submission workflow.

## GitHub Pages

This repo deploys the browser host with GitHub Actions. After the workflow succeeds, the static loader is available at:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

The Pages artifact also publishes hosted flavor folders:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/web/app.js"></script>
```

Manual Pages workflow runs can provide a version folder, such as `v0.1.0`, for stable consumption:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/v0.1.0/web/app.js"></script>
```

## License

PSWasm is available under the [MIT License](https://choosealicense.com/licenses/mit/).
