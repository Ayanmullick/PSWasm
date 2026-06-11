# PSWasm

PSWasm is a proof-of-concept browser WebAssembly runtime for a small PowerShell-compatible command surface.

The goal is to execute PowerShell text in a static web page without a build-time PowerShell-to-C# generation step. This repo starts with the narrow browser-safe pieces needed for the first package shape.

## Current Scope

The first runtime supports:

* browser-safe built-in commands: `Clear-Variable`, `ConvertFrom-Csv`, `ConvertFrom-Json`, `ConvertTo-Json`, `Format-List`, `Format-Table`, `Get-Command`, `Get-Culture`, `Get-Date`, `Get-Time`, `Get-TimeZone`, `Get-UICulture`, `Get-Variable`, `Invoke-WebRequest`, `Remove-Variable`, `Set-Variable`, `Write-*`
* tokenization into a browser-safe PowerShell token stream
* parsing into a small AST profile
* AST-based expression and command execution
* variable assignment
* arrays with comma literals, `@(...)`, ranges, indexing, negative indexes, and `Count` / `Length`
* hashtable literals with key indexing and `Count` / `Keys` / `Values`
* splatting with `@Params`
* expandable strings such as `"Hello $Name"`
* PowerShell region comments such as `#region` and `#endregion`
* script blocks with `$_` and `$PSItem` for browser-safe pipeline commands
* `if` / `elseif` / `else` for browser-safe conditional execution
* `foreach ($item in $items) { ... }` for browser-safe collection loops
* `while ($condition) { ... }` loops with a browser-safe iteration guard
* `for ($init; $condition; $iterator) { ... }` loops with a browser-safe iteration guard
* `switch ($value) { pattern { ... } default { ... } }` with exact, wildcard, default, `break`, and `continue` behavior
* browser-safe script functions with positional arguments, simple named parameters, `$args`, and `$input`
* `return`, `break`, and `continue` for browser-safe function and loop control flow
* `try` / `catch` / `finally` for browser-safe terminating runtime errors
* `throw` for script-level browser runtime errors
* simple member access such as `$_.Name` against hashtable-like objects
* `$env:Name` lookup through a browser-provided environment map
* simple named parameters
* arithmetic expressions with PowerShell-style precedence and parentheses
* grouped assignment expressions such as `($var = 1 + 2)`
* pipeline chain operators `&&` and `||`
* browser-safe operators adapted from PowerShell `TokenTraits`
* a basic object pipeline for registered browser commands
* a pluggable command registry

The browser-safe variable command set includes:

* `Clear-Variable`
* `Get-Variable`
* `Remove-Variable`
* `Set-Variable`

The aliases `clv`, `gv`, `rv`, and `sv` are also registered. These commands operate only on the current PSWasm runtime's in-memory session variables.

`Get-Command` lists the commands available in the current browser runtime. The `gcm` alias is also registered.

The browser-safe web command set includes:

* `Invoke-WebRequest`

The `iwr` alias is also registered. `Invoke-WebRequest` uses .NET `HttpClient`, which maps to the browser HTTP stack in browser-wasm. The browser-safe subset supports `-Uri`, `-Method`, `-CustomMethod`, `-Headers`, `-Body`, `-ContentType`, and `-SkipHttpErrorCheck`. It returns a hashtable-like response object with `StatusCode`, `StatusDescription`, `Headers`, `Content`, `RawContent`, and `RawContentLength`.

Browser security still applies. Cross-origin calls require the target service to allow the page's origin with CORS headers. PSWasm does not include desktop/server features such as `-OutFile`, `-InFile`, proxy configuration, certificate options, web sessions, local cookie containers, or CORS bypasses.

The browser-safe operator set currently includes:

* arithmetic/range: `+`, `-`, `*`, `/`, `%`, `..`
* grouped assignment and null coalescing: `($var = value)`, `??`
* logical and bitwise: `-not`, `-and`, `-or`, `-xor`, `-bnot`, `-band`, `-bor`, `-bxor`, `-shl`, `-shr`
* comparisons: `-eq`, `-ne`, `-gt`, `-ge`, `-lt`, `-le` and case-sensitive `-c*` variants
* wildcard/regex/string: `-like`, `-notlike`, `-match`, `-notmatch`, `-replace` and case-sensitive `-c*` variants
* collection/string helpers: `-contains`, `-notcontains`, `-in`, `-notin`, `-join`, `-split`, `-f`
* pipeline chain: `&&`, `||`

The runtime intentionally does not include the full PowerShell host, providers, native command execution, remoting, jobs, module autoloading, profiles, formatting data, help, or OS-specific APIs. It also does not reproduce the full `System.Management.Automation` parse-mode split between command mode and expression mode; the browser profile flattens those modes and maps each useful operator to browser-safe AST and executor behavior.

The browser-safe `Write-*` command set includes:

* `Write-Debug`
* `Write-Error`
* `Write-Host`
* `Write-Information`
* `Write-Output`
* `Write-Progress`
* `Write-Verbose`
* `Write-Warning`

`Write-Output` emits pipeline output. `Write-Host` writes visible host output. The other streams render as tagged browser output, such as `[Warning] message`, until a richer DOM stream UI is added.

The browser-safe object pipeline command set includes:

* `ConvertFrom-Json`
* `ConvertFrom-Csv`
* `ConvertTo-Json`
* `Format-List`
* `Format-Table`
* `ForEach-Object`
* `Get-Command`
* `Group-Object`
* `Measure-Object`
* `Out-String`
* `Select-Object`
* `Sort-Object`
* `Where-Object`

The aliases `ForEach`, `Group`, `Measure`, `Select`, `Sort`, and `Where` are also registered. Script blocks can use `$_` or `$PSItem` for the current pipeline item:

The `try` / `catch` / `finally` support follows the browser-safe subset of PowerShell terminating error handling. Untyped `catch` blocks catch PSWasm runtime errors, `finally` always runs, and caught errors are available as `$_` / `$PSItem` with fields such as `Message`, `Exception`, and `FullyQualifiedErrorId`.

```powershell
$value = 3
if ($value -gt 5) {
    'large'
} elseif ($value -eq 3) {
    'matched'
} else {
    'small'
}

foreach ($item in 1..3) {
    $item * 2
}

$i = 0
while ($i -lt 3) {
    $i = $i + 1
    $i
}

for ($j = 0; $j -lt 3; $j = $j + 1) {
    $j
}

switch ('browser') {
    'server' { 'server path' }
    'brow*' { 'browser path' }
    default { 'fallback path' }
}

function Add-Prefix($Text) {
    "pre-$Text"
}
Add-Prefix -Text 'browser'

function First-Matches($Items) {
    foreach ($item in $Items) {
        if ($item -lt 2) {
            continue
        }
        if ($item -gt 3) {
            break
        }
        $item
    }
    return 'done'
}
First-Matches @(1,2,3,4)

try {
    throw 'boom'
} catch {
    $_.Message
} finally {
    'cleanup'
}
Set-Variable -Name BrowserName -Value 'PSWasm'
Get-Variable BrowserName -ValueOnly
Get-Variable BrowserName | Format-List Name Value
Get-Command Format-* | Select-Object -ExpandProperty Name
Write-Output 'First' && Write-Output 'Second'
Write-Error 'Bad' || Write-Output 'Recovered'
$array = 22,5,10,8,12
$array.Count
$array[0]
$array[-1]
$array[1..2]
$table = @{Name='PSWasm'; Value=42}
$table['Name']
$table.Count
$table.Keys | Sort-Object
$response = Invoke-WebRequest -Uri 'https://api.github.com/zen' -Headers @{Accept='text/plain'} -SkipHttpErrorCheck
$response.StatusCode
$response.RawContentLength
1..4 | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 10 }
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}) | Select-Object -ExpandProperty Name
@{Name='browser'; Value=42} | ConvertTo-Json -Compress
'{"Name":"json","Value":7}' | ConvertFrom-Json | Select-Object -ExpandProperty Name
'Name,Value
csv,8' | ConvertFrom-Csv | Select-Object -ExpandProperty Name
@(@{Name='table-one'; Value=1}, @{Name='table-two'; Value=2}) | Format-Table Name Value
@{Name='list-sample'; Value=9} | Format-List
@(@{Name='three'; Value=3}, @{Name='one'; Value=1}, @{Name='two'; Value=2}) | Sort-Object Value
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}) | Measure-Object Value -Sum -Average
@('b','a','b') | Group-Object | Sort-Object Name
@{Name='one'; Value=1} | Out-String
```

The browser loader colors stream output by default:

* `Write-Error` renders red.
* `Write-Warning` renders orange.
* `Write-Information` renders green.

The generated DOM uses classes such as `pswasm-stream-error`, `pswasm-stream-warning`, and `pswasm-stream-information` so host pages can override the colors.

## Layout

```text
src/PowerShell.Wasm
  Browser-safe PowerShell parser, AST profile, executor, and command registry.

samples/BrowserHost
  User-facing static browser-wasm host that reads <script type="pwsh"> blocks at runtime.
  This is the sample published by GitHub Pages and the main reference for static web apps.

samples/ConsoleSmoke
  Maintainer/agent smoke harness for the runtime without the WebAssembly workload.
  This is not an end-user sample. It exists for quick parser, operator, command, and host-command checks while changing PSWasm.

tests/PowerShell.Wasm.Verify
  Assertion-based runtime checks for operators, streams, built-ins, splatting, and object pipelines.
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

Publish the browser sample:

```powershell
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o publish /p:UseAppHost=false
```

The static files are emitted under:

```text
publish/wwwroot
```

## Maintainer Checks

These checks are for maintainers and agents changing PSWasm internals. They are not required to use the browser sample.

Run the console smoke harness:

```powershell
dotnet run --project .\samples\ConsoleSmoke\ConsoleSmoke.csproj
```

Run assertion-based runtime verification:

```powershell
dotnet run --project .\tests\PowerShell.Wasm.Verify\PowerShell.Wasm.Verify.csproj
```

## Browser POC

The browser sample reads inline PowerShell blocks like this at runtime. Importing `app.js` automatically runs every `<script type="pwsh">` block on the page and inserts a `<pre class="pswasm-output">` output block after each script. Normal HTML between scripts, such as `<hr>`, is still rendered by the browser.

Static pages and browser playgrounds such as CodePen should import `app.js`. The `app.d.ts` file is only a TypeScript declaration file for editors and build tools; browsers do not execute it.

```html
<script type="pwsh">
Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Write-Warning 'Browser-visible warning'
Write-Output (2 + 2)
</script>

<hr>

<script type="pwsh">
1..4 | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 10 }
@{Name='browser'; Value=42} | ConvertTo-Json -Compress
</script>

<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

If a page needs one combined output area instead, set `window.pswasmDisableAutoRun = true` before importing `app.js`, then call `runPowerShellScripts({ output: "#output" })` explicitly.

Static apps can also call the browser API directly:

```html
<pre id="output">Starting...</pre>

<script type="module">
  window.pswasmDisableAutoRun = true;

  const { executePowerShell, renderPowerShellOutput } =
    await import("https://ayanmullick.github.io/PSWasm/app.js");

  const output = await executePowerShell(`
Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Write-Information 'Executed from JavaScript'
Write-Output (2 + 2)
  `);

  renderPowerShellOutput(output, "#output");
</script>
```

For stream-aware rendering, use the structured result API:

```javascript
const { executePowerShellResult, renderPowerShellResult } =
  await import("https://ayanmullick.github.io/PSWasm/app.js");

const result = await executePowerShellResult("Write-Warning 'Check this'");
// result.records: [{ stream: "Warning", text: "Check this" }]
renderPowerShellResult(result, "#output");
```

The published browser module includes `app.d.ts` beside `app.js` for editors and static-app tooling. The public API shape is:

```typescript
executePowerShell(script, options)       // Promise<string>
executePowerShellResult(script, options) // Promise<PowerShellWasmResult>
runPowerShellScripts(options)            // Promise<void>
renderPowerShellResult(result, target)   // void
renderPowerShellOutput(text, target)     // void
```

If a static host or CodePen keeps an old browser bundle in cache after a new deployment, add a version query string:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js?v=latest"></script>
```

The published browser host is intentionally generic and does not include app-specific commands. If an application needs custom C# commands, build a custom host that registers those commands before executing scripts.

For browser POCs, environment values can be passed from JavaScript:

```javascript
window.pswasmEnvironment = {
  DemoValue: "..."
};
```

Do not treat this as a production secret boundary. Any value sent to a static browser app is visible to the browser user.

## GitHub Pages

This repo deploys the browser host with GitHub Actions. After the workflow succeeds, the static loader is available at:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

## Direction

This is a carve-out toward a browser-compatible PowerShell profile. The runtime now has the same broad layers as the PowerShell engine: tokenizer, parser, AST, session state, command dispatch, parameter binding, splatting, expression evaluation, and a basic pipeline.

The implementation keeps source-reference comments in the new language/runtime files so future work can compare against the original PowerShell repository:

* `src/System.Management.Automation/engine/parser/token.cs`
* `src/System.Management.Automation/engine/parser/tokenizer.cs`
* `src/System.Management.Automation/engine/parser/Parser.cs`
* `src/System.Management.Automation/engine/parser/ast.cs`
* `src/System.Management.Automation/engine/SessionState*.cs`
* `src/System.Management.Automation/engine/CommandProcessor*.cs`
* `src/System.Management.Automation/engine/ParameterBinder*.cs`

This repo should continue copying or adapting those designs only where they can stay browser-safe. The operator vocabulary and precedence are adapted from `TokenTraits`; execution semantics live in the PSWasm browser executor. Full `System.Management.Automation` parity still requires explicitly excluding desktop/server features such as providers, native process execution, remoting, jobs, module autoloading, and host UI APIs.
