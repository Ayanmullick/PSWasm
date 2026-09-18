# Browser DOM Smoke

This smoke test runs the published BrowserHost in a browser and returns a structured report to a waiting PowerShell command. It covers DOM event callbacks and storage bindings, JavaScript runtime/session APIs, external script ordering, browser errors, and cleanup that restores the affected storage values. The expected assertions are listed in [checks.json](checks.json). It does not exercise a live Azure sign-in.

## Prerequisites

Use PowerShell 7, the .NET SDK selected by [global.json](../../global.json), the `wasm-tools` workload, `dotnet-serve`, and a browser with WebAssembly support and developer tools. The browser step can be run manually in DevTools or through browser automation; it does not require a particular editor or coding agent.

Run commands from the repository root. BrowserHost must have restore assets for the configuration being tested because the coordinator publishes with `--no-restore`. For a fresh checkout, restore the default Release configuration first:

```powershell
dotnet restore ./samples/BrowserHost/PSWasm.BrowserHost.csproj -r browser-wasm -p:Configuration=Release
```

## Start and Run the Check

Start the CLI and leave it running. This example gives a manual DevTools run ten minutes to submit its report:

```powershell
pwsh -NoProfile -File ./tests/BrowserDomSmoke/Invoke-BrowserDomSmoke.ps1 -TimeoutSeconds 600
```

The command publishes BrowserHost, stages an isolated site under `.WorkDir/TestResults/BrowserDomSmoke/<runId>/site/`, starts its own loopback `dotnet serve`, and prints a run ID and URL.

New run IDs use UTC `yyyyMMdd-HHmmssZ`, for example `20260917-143025Z`. If that name already exists, allocation selects the first unused suffix from `-02` through `-999`; it never reuses an existing run directory or file. An exclusive lock serializes allocation across script processes. The empty `.WorkDir/TestResults/BrowserDomSmoke/.run-id.lock` file is intentionally persistent and must not be deleted while allocators may be running.

Existing GUID-named runs and their evidence remain untouched. Legacy GUID IDs are still accepted for report validation, subject to the same exact run-ID match, waiting-state, and deadline checks.

| Option | Purpose |
| --- | --- |
| `-Configuration Release` | Select the build configuration; the default is `Release`. |
| `-SkipPublish` | Reuse an already current BrowserHost publish. |
| `-Port 5010` | Select an unused port; the default is `5010`. |
| `-TimeoutSeconds 180` | Set the report wait after the server starts; the default is `180` seconds. |

Open the printed URL in a new browser tab. In that page's DevTools console, run:

```javascript
const report = await globalThis.runPSWasmBrowserSmoke();
JSON.stringify(report, null, 2);
```

Copy the resulting JSON text into `.WorkDir/TestResults/BrowserDomSmoke/<runId>/submission.json`. Save the report object itself as UTF-8 JSON, without console labels, surrounding string quotes, Markdown fences, or an automation-tool response envelope. Browser automation can save the same returned object directly. The report also remains available as `globalThis.pswasmBrowserSmokeResult` if you need to copy it again.

## Submit the Report

In another shell, submit the saved report while the first command is still waiting:

```powershell
pwsh -NoProfile -File ./tests/BrowserDomSmoke/Invoke-BrowserDomSmoke.ps1 `
  -ResultPath ./.WorkDir/TestResults/BrowserDomSmoke/<runId>/submission.json
```

Replace `<runId>` with the printed ID. `-ResultPath` accepts an absolute path under `.WorkDir/TestResults/BrowserDomSmoke/` or a path relative to the current directory. The script also accepts `-ResultJson '<JSON>'` when the report is quoted correctly.

Submission validates the schema, run ID, timestamps, and expected assertions. A passing report must contain every expected check in order with no failures or errors. Rejected submissions do not complete the waiting run. An accepted report is written to that run's `result.json`.

Wait for the original CLI to finish. It exits `0` for a validated pass or `1` for a failed report, timeout, or server cleanup failure. It stops only the server it started. Close the test tab after capturing the report.

## Run-ID Regression Checks

After changing run naming, allocation, or report routing, run this dependency-free check:

```powershell
pwsh -NoProfile -File ./tests/BrowserDomSmoke/Test-RunIdentity.ps1
```

It tests the coordinator's actual helper functions for timestamp and legacy GUID validation, exact report matching, collision preservation, concurrent allocation, path containment, and projected path lengths. JSON evidence stays under `.WorkDir/TestResults/RunIdentity/`. This check does not launch a browser or replace the browser smoke, failed-report, timeout, and server-cleanup checks required for orchestration changes.

## Local Evidence and Scope

Each run retains `run.json`, its staged `site/`, the captured `submission.json`, `result.json`, and `server.out.log` / `server.err.log` under `.WorkDir/TestResults/BrowserDomSmoke/<runId>/`. Handled CLI failures and report timeouts are recorded as failed results. An externally terminated CLI can leave an incomplete run; that is not a pass.

The browser assertion module cleans up its events, temporary nodes, sessions, and affected storage values.

The CLI stages and serves the test site, validates the returned report, and manages its server. A person or browser automation must run the browser assertions and submit the report; starting the CLI alone does not complete an unattended browser check.
