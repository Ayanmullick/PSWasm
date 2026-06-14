# Browser DOM Smoke

This smoke test validates the real browser DOM bridge:

* published `samples/BrowserHost` assets
* `app.js`
* `JSExport` / `JSImport` wiring
* `Register-DomEvent`
* `Get-DomValue`
* `Set-DomText`
* `Set-DomProperty`
* browser event callback execution into a PowerShell script block

Run the automated check when a headless Edge, Chrome, or Chromium instance can be launched:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1
```

Use manual mode when validating through Microsoft Edge Tools for VS Code or the VS Code integrated browser:

```powershell
.\tests\BrowserDomSmoke\Invoke-BrowserDomSmoke.ps1 -Manual
```

Open the printed URL, verify the status starts as `DOM event handler ready.`, change the name field, click the button, and verify the status text updates without browser console errors.
