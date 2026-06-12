# PSWasm Agent Instructions

## Repository Mission

Build PSWasm as a browser-safe PowerShell-compatible WebAssembly runtime package that can be imported into static web apps without requiring a build-time PowerShell-to-C# conversion step.

Keep the repository generic. Do not add app-specific samples or product-specific flavor such as Cosmos DB.

When adding browser-safe PowerShell concepts, keep syntax and behavior as close as practical to the original PowerShell behavior from the `PowerShell/PowerShell` repository. Prefer adapting PowerShell concepts, naming, operator semantics, parser behavior, stream behavior, and command behavior where they make sense in a browser DOM/WebAssembly context.

If full PowerShell behavior depends on desktop/server features such as providers, native processes, remoting, jobs, profiles, host UI APIs, or unrestricted filesystem access, implement only the browser-safe subset and document the limitation.

## Shell Commands

Always run PowerShell through `pwsh.exe -NoProfile`. Do not run PowerShell commands without `-NoProfile`.

## Validation

Use the verification project before considering runtime changes complete:

```pwsh
dotnet run --project .\tests\PowerShell.Wasm.Verify\PowerShell.Wasm.Verify.csproj --configuration Release

```