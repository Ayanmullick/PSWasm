# Browser Flavor Smoke

This smoke test validates PSWasm browser flavor packaging.

It checks:

* the core runtime build excludes optional DOM, web, crypto, and Azure auth command behavior
* clean browser flavor publish output has the importable package shape
* default flavor output includes `app.js`, `app.d.ts`, and `_framework/**`
* default flavor output does not include the BrowserHost sample `index.html` or sample `.ps1` files
* hosted flavor aliases have the `/<flavor>/app.js` shape
* versioned hosted flavor aliases have the `/v-smoke/<flavor>/app.js` shape
* `core` omits optional DOM, web-request, crypto, and Azure auth assets
* `web` includes browser DOM and web-request assets without the PSWasm crypto bridge
* `AzAuth` includes browser DOM, web-request, crypto, and the lazy MSAL auth bridge
* `full` includes all public browser feature groups

Run it from the repository root:

```powershell
.\tests\BrowserFlavorSmoke\Invoke-BrowserFlavorSmoke.ps1 -NoRestore
```
