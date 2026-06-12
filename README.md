# PSWasm

PSWasm is a proof-of-concept browser WebAssembly runtime for a small PowerShell-compatible command surface.

The goal is to execute PowerShell text in a static web page without a build-time PowerShell-to-C# generation step. This repo starts with the narrow browser-safe pieces needed for the first package shape.

## Current Scope

The first runtime supports:

* browser-safe built-in commands: `Clear-Variable`, `ConvertFrom-Csv`, `ConvertFrom-Json`, `ConvertTo-Json`, `Format-List`, `Format-Table`, `Get-Command`, `Get-Culture`, `Get-Date`, `Get-DomSession`, `Get-Time`, `Get-TimeZone`, `Get-UICulture`, `Get-Variable`, `Invoke-WebRequest`, `New-DomSession`, `Remove-DomSession`, `Remove-Variable`, `Select-String`, `Set-Variable`, `Write-*`
* tokenization into a browser-safe PowerShell token stream
* parsing into a small AST profile
* AST-based expression and command execution
* variable assignment plus browser-safe automatic and preference variable subsets
* parallel variable assignment such as `$Name,$Env,$Location = 'AzReport','Dev','NorthCentralUS'`
* arrays with comma literals, `@(...)`, ranges, indexing, negative indexes, and `Count` / `Length`
* hashtable literals with key indexing and `Count` / `Keys` / `Values`
* splatting with hashtables, arrays, `$PSBoundParameters`, and `$args`
* expandable strings such as `"Hello $Name"`
* PowerShell region comments such as `#region` and `#endregion`
* script blocks with `$_` and `$PSItem` for browser-safe pipeline commands
* `if` / `elseif` / `else` for browser-safe conditional execution
* `foreach ($item in $items) { ... }` for browser-safe collection loops
* `while ($condition) { ... }` loops with a browser-safe iteration guard
* `for ($init; $condition; $iterator) { ... }` loops with a browser-safe iteration guard
* `switch ($value) { pattern { ... } default { ... } }` with exact, wildcard, regex, default, `break`, and `continue` behavior
* browser-safe script functions with positional arguments, simple named parameters, `$args`, and `$input`
* `return`, `break`, and `continue` for browser-safe function and loop control flow
* `try` / `catch` / `finally` for browser-safe terminating runtime errors
* `throw` for script-level browser runtime errors
* simple member access such as `$_.Name` against hashtable-like objects
* `$env:Name` lookup through a browser-provided environment map
* simple named parameters
* browser-safe common parameter handling for stream preferences and variable capture
* arithmetic expressions with PowerShell-style precedence and parentheses
* grouped assignment expressions such as `($var = 1 + 2)`
* postfix variable statements such as `$i++` and `$i--`
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

The browser-safe splatting subset includes:

* hashtable splatting for named parameters, such as `Write-Output @{InputObject='value'}`
* array splatting for positional arguments, such as `Invoke-Thing @Arguments`
* multiple splatted objects in one command
* explicit named parameters overriding splatted hashtable values
* forwarding script-function parameters with `@PSBoundParameters`
* forwarding unbound positional arguments with `@args`

PSWasm does not include advanced-function metadata such as `CmdletBinding`, DSC resources, or native command splatting behavior.

The browser-safe automatic variable subset includes:

* constants and status: `$true`, `$false`, `$null`, `$?`
* error and matching state: `$Error`, `$StackTrace`, `$Matches`
* current pipeline and function state: `$_`, `$PSItem`, `$args`, `$input`, `$PSBoundParameters`
* culture and runtime identity: `$PSCulture`, `$PSUICulture`, `$PSEdition`, `$PSVersionTable`, `$Host`, `$ShellId`
* virtual browser locations: `$PWD`, `$HOME`, `$PSHOME`, `$PSCommandPath`, `$PSScriptRoot`
* browser-safe platform/runtime flags: `$IsCoreCLR`, `$IsLinux`, `$IsMacOS`, `$IsWindows`, `$EnabledExperimentalFeatures`, `$NestedPromptLevel`, `$PSDebugContext`

PSWasm intentionally does not add automatic variables that imply desktop host state, profiles, native process execution, remoting, eventing, jobs, or real providers, such as `$PID`, `$PROFILE`, `$LASTEXITCODE`, `$PSEventArgs`, `$Sender`, and `$PSSenderInfo`.

The browser-safe preference variable subset includes:

* stream behavior: `$DebugPreference`, `$ErrorActionPreference`, `$InformationPreference`, `$ProgressPreference`, `$VerbosePreference`, `$WarningPreference`
* string expansion: `$OFS`
* initialized session preferences: `$ConfirmPreference`, `$ErrorView`, `$FormatEnumerationLimit`, `$OutputEncoding`, `$PSDefaultParameterValues`, `$WhatIfPreference`

The stream preferences support `Continue`, `SilentlyContinue`, `Ignore`, and `Stop` for PSWasm `Write-*` commands. Interactive/debugger/workflow values such as `Inquire`, `Break`, and `Suspend` stop with a browser runtime error instead of prompting. Preferences tied to host history, logging, email, modules, native commands, remoting, styles, or transcripts are intentionally not included yet, such as `$MaximumHistoryCount`, `$PSModuleAutoLoadingPreference`, `$PSNativeCommandArgumentPassing`, `$PSSessionOption`, `$PSStyle`, and `$Transcript`.

The browser-safe common parameter subset includes:

* command-local stream overrides: `-Debug`, `-Verbose`, `-ErrorAction`, `-InformationAction`, `-ProgressAction`, `-WarningAction`
* variable capture: `-OutVariable`, `-ErrorVariable`, `-InformationVariable`, `-WarningVariable`, `-PipelineVariable`
* aliases: `-db`, `-vb`, `-ea`, `-infa`, `-proga`, `-wa`, `-ov`, `-ev`, `-iv`, `-wv`, `-pv`, `-ob`, `-wi`, `-cf`
* append capture syntax such as `-OutVariable +captured`
* accepted no-op parameters for browser commands that do not use them yet: `-OutBuffer`, `-WhatIf`, `-Confirm`

`-PipelineVariable` / `-pv` is available as a browser subset that captures the command output into the named variable. It does not yet reproduce PowerShell's full item-by-item, pipeline-scoped streaming semantics.

`Get-Command` lists the commands available in the current browser runtime. The `gcm` alias is also registered.

The browser-safe DOM session command set includes:

* `New-DomSession`
* `Get-DomSession`
* `Remove-DomSession`

These commands create and manage in-memory browser DOM session handles with `Id`, `Name`, `Target`, `State`, and `SessionType` fields. They are modeled as a browser-safe session concept for PowerShell users; they do not expose unrestricted DOM APIs or desktop/session remoting.

The browser-safe web command set includes:

* `Invoke-WebRequest`

The `iwr` alias is also registered. `Invoke-WebRequest` uses .NET `HttpClient`, which maps to the browser HTTP stack in browser-wasm. The browser-safe subset supports `-Uri`, `-Method`, `-CustomMethod`, `-Headers`, `-Body`, `-ContentType`, and `-SkipHttpErrorCheck`. It returns a hashtable-like response object with `StatusCode`, `StatusDescription`, `Headers`, `Content`, `RawContent`, and `RawContentLength`.

Browser security still applies. Cross-origin calls require the target service to allow the page's origin with CORS headers. PSWasm does not include desktop/server features such as `-OutFile`, `-InFile`, proxy configuration, certificate options, web sessions, local cookie containers, or CORS bypasses.

The browser-safe operator set currently includes:

* arithmetic/range: `+`, `-`, `*`, `/`, `%`, `..`
* grouped assignment, postfix variable statements, and null coalescing: `($var = value)`, `$var++`, `$var--`, `??`
* logical and bitwise: `-not`, `-and`, `-or`, `-xor`, `-bnot`, `-band`, `-bor`, `-bxor`, `-shl`, `-shr`
* comparisons: `-eq`, `-ne`, `-gt`, `-ge`, `-lt`, `-le` and case-sensitive `-c*` variants
* wildcard/regex/string: `-like`, `-notlike`, `-match`, `-notmatch`, `-replace` and case-sensitive `-c*` variants
* collection/string helpers: `-contains`, `-notcontains`, `-in`, `-notin`, `-join`, `-split`, `-f`
* pipeline chain: `&&`, `||`

The browser-safe regular expression subset uses the .NET regex engine and includes:

* `-match`, `-notmatch`, `-replace`, and `-split`
* case-sensitive variants: `-cmatch`, `-cnotmatch`, `-creplace`, and `-csplit`
* `$Matches` for numeric and named captures from `-match` and `switch -Regex`
* `switch -Regex` and `switch -Regex -CaseSensitive`
* `Select-String` / `sls` with `-Pattern`, `-InputObject`, `-CaseSensitive`, `-NotMatch`, and `-AllMatches`

PSWasm does not expose the static `[regex]` type, file-backed `Select-String`, full `MatchInfo` formatting, or every .NET regex option yet.

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
* `Select-String`
* `Sort-Object`
* `Where-Object`

The aliases `ForEach`, `Group`, `Measure`, `Select`, `sls`, `Sort`, and `Where` are also registered. Script blocks can use `$_` or `$PSItem` for the current pipeline item:

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

The browser host is intended to be PowerShell-first. Static pages write PowerShell in `<script type="pwsh">` blocks and import `app.js` once at the end of the page. The import runs the PowerShell blocks at runtime and inserts a `<pre class="pswasm-output">` output block after each script. Normal HTML between scripts, such as `<hr>`, is still rendered by the browser.

Inline PowerShell blocks share one PSWasm runtime session by default. Variables, functions, preferences, `$Error`, and DOM session handles created in an earlier block are available to later blocks.

```html
<script type="pwsh">
$Dom = New-DomSession -Name Main -Target '#app'
function Write-BrowserSummary {
    Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    'Target=' + $Dom.Target
    Get-Command *-DomSession | Select-Object -ExpandProperty Name
}
</script>

<hr>

<script type="pwsh">
Write-BrowserSummary
Get-DomSession Main | Select-Object Id Name Target State
Remove-DomSession Main
</script>

<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js"></script>
```

The published browser module includes `app.d.ts` beside `app.js` for editors and static-app tooling. The JavaScript API is still available for custom hosts, but the default static-page path does not require creating JavaScript sessions.

If a static host or CodePen keeps an old browser bundle in cache after a new deployment, add a version query string:

```html
<script type="module" src="https://ayanmullick.github.io/PSWasm/app.js?v=latest"></script>
```

The published browser host is intentionally generic and does not include app-specific commands. If an application needs custom C# commands, build a custom host that registers those commands before executing scripts.

For browser POCs, environment values can be passed with one browser global before importing `app.js` and read from PowerShell with `$env:Name`:

```html
<script>
window.pswasmEnvironment = {
  DemoValue: "..."
};
</script>
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
