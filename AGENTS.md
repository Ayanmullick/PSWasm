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

For browser host or browser-wasm interop changes, also publish the browser host:

```pwsh
dotnet publish .\samples\BrowserHost\PSWasm.BrowserHost.csproj -c Release -r browser-wasm -o .\artifacts\BrowserHost /p:UseAppHost=false --no-restore
```

When browser package size or feature gating changes, publish clean browser flavors and measure the payload:

```pwsh
.\tools\Publish-BrowserFlavors.ps1 -Flavor core,dom,crypto,web,dom-web-crypto -NoRestore
.\tools\Measure-BrowserPayload.ps1 -Path .\artifacts\BrowserFlavors\dom-web-crypto\wwwroot -SummaryOnly
```

Also run the browser flavor smoke test when browser package size, publish shape, or feature gating changes:

```pwsh
.\tests\BrowserFlavorSmoke\Invoke-BrowserFlavorSmoke.ps1 -NoRestore
```

For manual BrowserHost validation, prefer `dotnet serve` over ad hoc static servers:

```pwsh
dotnet serve -d .\artifacts\BrowserHost\wwwroot -p 5010
```

If port 5010 is already in use, choose another free port and state the URL used.

For DOM command or browser DOM bridge changes, run the browser DOM smoke test:

```pwsh
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1
```

If headless Edge/Chrome cannot be launched in the current environment, run the manual smoke server and open the printed URL in Microsoft Edge Tools for VS Code or the VS Code integrated browser:

```pwsh
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1 -Manual
```

## Documentation Updates

After successful validation for future feature changes, update the relevant documentation and samples when behavior, commands, APIs, or user-facing workflow changed:

* Update `README.md` with short entry-point guidance and links to the detailed wiki page.
* Update the GitHub Wiki page, sidebar, or validation page when the detailed behavior belongs outside the README.
* Update `samples/BrowserHost/wwwroot/index.html` when a generic browser-safe feature needs a runnable browser sample.

Keep documentation generic. Do not add app-specific examples or product-specific flavor such as Cosmos DB to this repository.
