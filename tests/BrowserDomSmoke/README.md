# Browser DOM Smoke

This smoke test runs the published BrowserHost in the VS Code `integrated_browser` MCP and returns a structured report to a waiting PowerShell command. It covers DOM event callbacks and storage bindings, JavaScript runtime/session APIs, external script ordering, browser errors, and cleanup that restores the affected storage values. The expected assertions are listed in [checks.json](checks.json). It does not exercise a live Azure sign-in.

## Prerequisites

Use an MCP-connected agent or session to orchestrate the test. Call the `integrated_browser` MCP's `browser_status` before starting. An unavailable MCP blocks the check; there is no standalone Edge, Chrome, or Chromium fallback.

The .NET SDK, WebAssembly workload, and `dotnet serve` must already be installed, and BrowserHost must have restore assets for its current configuration. The script publishes with `--no-restore`. Do not install tools or restore/download dependencies without explicit user approval.

Run shell commands from the PSWasm repository root. On Windows, use `pwsh.exe -NoProfile`.

The coordinator uses the shared workspace path preflight for its publish, staging, and report paths. Documented Windows Cloud Files tags are allowed, but redirects, unsupported tags, and metadata-query errors stop the check. Keep all workspace files locally available before running the test; availability is a user-managed prerequisite and is not checked automatically. See [Workspace path safety](https://github.com/Ayanmullick/PSWasm/wiki/Build-and-Validation#workspace-path-safety) for the limits of this preflight and post-move validation.

## Start and Run the Check

Start the CLI and leave it running:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1
```

The command publishes BrowserHost, stages an isolated site under `.WorkDir/TestResults/BrowserDomSmoke/<runId>/site/`, starts its own `dotnet serve`, and prints a run ID and URL.

New run IDs use UTC `yyyyMMdd-HHmmssZ`, for example `20260917-143025Z`. If that name already exists, allocation selects the first unused suffix from `-02` through `-999`; it never reuses an existing run directory or file. An exclusive lock serializes allocation across script processes. The empty `.WorkDir/TestResults/BrowserDomSmoke/.run-id.lock` file is intentionally persistent and must not be deleted while allocators may be running.

Existing GUID-named runs and their evidence remain untouched. Legacy GUID IDs are still accepted for report validation, subject to the same exact run-ID match, waiting-state, and deadline checks.

| Option | Purpose |
| --- | --- |
| `-SkipPublish` | Reuse an already current BrowserHost publish. |
| `-Port 5010` | Select an unused port; the default is `5010`. |
| `-TimeoutSeconds 180` | Set the report wait after the server starts; the default is `180` seconds. |

Open the printed URL in a dedicated `integrated_browser` MCP tab. Preserve existing user tabs. Evaluate this expression in the test tab:

```javascript
await globalThis.runPSWasmBrowserSmoke()
```

The MCP orchestrator must capture the returned report object and save its JSON to the printed run directory's `submission.json`. Save the object itself, without an MCP response envelope, Markdown, or a prose summary.

## Submit the Report

In another shell, submit the saved report while the first command is still waiting:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1 `
  -ResultPath .\.WorkDir\TestResults\BrowserDomSmoke\<runId>\submission.json
```

Replace `<runId>` with the printed ID. `-ResultPath` accepts an absolute path under `.WorkDir/TestResults/BrowserDomSmoke/` or a path relative to the current directory. The script also accepts `-ResultJson '<JSON>'` when the report is quoted correctly.

Submission validates the schema, run ID, timestamps, and expected assertions. A passing report must contain every expected check in order with no failures or errors. Rejected submissions do not complete the waiting run. An accepted report is written to that run's `result.json`.

Wait for the original CLI to finish. It exits `0` for a validated pass or `1` for a failed report, timeout, or server cleanup failure. It stops only the server it started. Close only the MCP tab opened for this run after report capture.

## Run-ID Regression Checks

After changing run naming, allocation, or report routing, run this dependency-free check from a `pwsh.exe -NoProfile` shell:

```powershell
.\tools\Invoke-WorkspaceCommand.ps1 pwsh -NoProfile -File .\tests\BrowserDomSmoke\Test-RunIdentity.ps1
```

It tests the coordinator's actual helper functions for timestamp and legacy GUID validation, exact report matching, collision preservation, concurrent allocation, path containment, and projected path lengths. JSON evidence stays under `.WorkDir/TestResults/RunIdentity/`. This check does not launch a browser or replace the integrated-browser smoke, failed-report, timeout, and server-cleanup checks required for orchestration changes.

## Local Evidence and Scope

Each run retains `run.json`, its staged `site/`, the captured `submission.json`, `result.json`, and `server.out.log` / `server.err.log` under `.WorkDir/TestResults/BrowserDomSmoke/<runId>/`. Handled CLI failures and report timeouts are recorded as failed results. An externally terminated CLI can leave an incomplete run; that is not a pass.

The CLI scopes child tools' temporary storage through `tools/Invoke-WorkspaceCommand.ps1`; the browser profile remains VS Code-owned, and SDK/tool caches remain shared. The browser assertion module cleans up its events, temporary nodes, sessions, and affected storage values.

This is an MCP-orchestrated workflow. The CLI stages and serves the test site, validates the returned report, and manages its server. It neither calls MCP itself nor runs an unattended browser check from a single shell command.
