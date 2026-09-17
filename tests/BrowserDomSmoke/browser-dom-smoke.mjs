// Browser-only: importing this module exposes the runner without starting a test.
export function isSmokeRunId(value) {
  if (typeof value !== "string") return false;
  if (/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value) && value.length === 36) return true;
  const match = /^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})Z(?:-(?:0[2-9]|[1-9][0-9]{1,2}))?$/.exec(value);
  if (!match || match[0] !== value || match[1] === "0000") return false;
  const iso = `${match[1]}-${match[2]}-${match[3]}T${match[4]}:${match[5]}:${match[6]}.000Z`;
  const date = new Date(iso);
  return Number.isFinite(date.getTime()) && date.toISOString() === iso;
}

let runPromise;
globalThis.runPSWasmBrowserSmoke = (options = {}) => runPromise ??= runSmoke(options);

async function runSmoke(options) {
  const report = {
    schemaVersion: 1, runId: new URL(location.href).searchParams.get("runId") ?? "", status: "failed",
    startedAt: new Date().toISOString(), finishedAt: "", checks: [], errors: []
  };
  const timeoutMs = options?.timeoutMs ?? 45_000;
  const deadline = performance.now() + timeoutMs;
  const storageKeys = ["pswasm.domSmokeName", "pswasm.domSmokeTemporary"];
  const storedValues = new Map(), sessions = new Set(), pendingOperations = new Set(), nodes = [], bridgeHooks = [];
  const events = new Set(), bindings = new Set();
  const originalAutoRun = Object.getOwnPropertyDescriptor(globalThis, "pswasmDisableAutoRun");
  const originalConsoleError = console.error;
  const originalStyle = document.getElementById("pswasm-default-styles");
  let api, domSession, apiSession, expectedChecks, fixture, stopped = false;

  const captureError = event => report.errors.push(event.error ? errorText(event.error) :
    event.message || `Resource error: ${event.target?.src || event.target?.href || "unknown resource"}`);
  const captureRejection = event => report.errors.push(`Unhandled rejection: ${errorText(event.reason)}`);
  const consoleHook = (...args) => {
    report.errors.push(`console.error: ${args.map(errorText).join(" ")}`);
    Reflect.apply(originalConsoleError, console, args);
  };
  console.error = consoleHook;
  window.addEventListener("error", captureError, true);
  window.addEventListener("unhandledrejection", captureRejection);

  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const ensureTime = () => {
    if (stopped || performance.now() >= deadline) throw new Error(`Browser smoke timed out after ${timeoutMs} ms.`);
  };
  async function bounded(task) {
    const source = Promise.resolve(task);
    pendingOperations.add(source);
    source.then(() => pendingOperations.delete(source), () => pendingOperations.delete(source));
    ensureTime();
    let timer;
    try {
      const result = await Promise.race([source, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`Browser smoke timed out after ${timeoutMs} ms.`)),
          Math.max(1, deadline - performance.now()));
      })]);
      ensureTime();
      return result;
    } finally { clearTimeout(timer); }
  }
  async function check(name, action) {
    const entry = { name, status: "failed" };
    report.checks.push(entry);
    try {
      const details = await bounded(Promise.resolve().then(() => { ensureTime(); return action(); }));
      entry.status = "passed";
      if (details !== undefined) entry.details = details;
    } catch (error) { entry.error = errorText(error); throw error; }
  }
  async function waitFor(predicate, message) {
    while (!predicate()) {
      ensureTime();
      await bounded(new Promise(resolve => setTimeout(resolve, 20)));
    }
    assert(!stopped, message);
  }
  async function createSession(environment = {}) {
    ensureTime();
    return bounded(api.createPowerShellSession({ environment }).then(async session => {
      sessions.add(session);
      // A creation that finishes after a timeout must not leak an unreachable session.
      if (stopped) { await session.dispose(); sessions.delete(session); }
      return session;
    }));
  }
  function addNode(tag, parent = document.body) {
    const node = document.createElement(tag);
    nodes.push(node);
    parent.append(node);
    return node;
  }
  function trackBridge(method, ids) {
    const bridge = globalThis.pswasmDom, original = bridge[method];
    const hook = (id, ...args) => {
      ids.add(id);
      return Reflect.apply(original, bridge, [id, ...args]);
    };
    bridge[method] = hook;
    bridgeHooks.push({ bridge, method, original, hook });
  }

  try {
    await check("fixture-elements", () => {
      assert(Number.isFinite(timeoutMs) && timeoutMs > 0 && timeoutMs <= 120_000,
        "timeoutMs must be a positive number no greater than 120000.");
      assert(isSmokeRunId(report.runId), "The URL must contain a UTC run name or legacy GUID runId.");
      const required = selector => {
        const node = document.querySelector(selector);
        assert(node, `Missing required fixture element: ${selector}`);
        return node;
      };
      fixture = {
        form: required("#dom-sample-form"), input: required("#dom-sample-name"), button: required("#dom-sample-button"),
        status: required("#dom-sample-status"), html: required("#dom-sample-html"), script: required("#dom-smoke-pwsh")
      };
      fixture.snapshot = {
        value: fixture.input.value, disabled: fixture.button.disabled,
        status: fixture.status.textContent, html: fixture.html.innerHTML
      };
      for (const key of storageKeys) storedValues.set(key, localStorage.getItem(key));
      for (const key of storageKeys) localStorage.removeItem(key);
    });

    await check("module-import", async () => {
      globalThis.pswasmDisableAutoRun = true;
      const manifest = await bounded(fetch(new URL("./checks.json", import.meta.url)));
      assert(manifest.ok, `Unable to load checks.json (${manifest.status}).`);
      expectedChecks = await bounded(manifest.json());
      assert(Array.isArray(expectedChecks) && expectedChecks.length > 0 &&
        expectedChecks.every(name => typeof name === "string"), "Invalid smoke check manifest.");
      api = await bounded(import("/app.js"));
      for (const name of ["executePowerShell", "executePowerShellResult", "createPowerShellSession",
        "executePowerShellSession", "executePowerShellSessionResult", "disposePowerShellSession",
        "runPowerShellScripts", "renderPowerShellResult"]) {
        assert(typeof api[name] === "function", `Missing browser API export: ${name}`);
      }
      trackBridge("registerEvent", events);
      trackBridge("registerStorageBinding", bindings);
    });

    await check("dom-readiness", async () => {
      domSession = await createSession();
      const output = addNode("pre");
      output.hidden = true;
      await bounded(api.runPowerShellScripts({ selector: "#dom-smoke-pwsh", session: domSession, output }));
      assert(!output.querySelector(".pswasm-stream-error"), `Fixture script returned an error: ${output.textContent}`);
      await waitFor(() => fixture.status.textContent === "DOM event handler ready.", "DOM handler did not become ready.");
      assert(fixture.input.value === "PSWasm", "DOM value/property bridge did not initialize the input.");
    });
    await check("dom-table-encoding", () => {
      const cells = Array.from(fixture.html.querySelectorAll("td"), cell => cell.textContent);
      assert(cells.join("|") === "PowerShell|<Ready>", `Unexpected table cells: ${cells.join("|")}`);
      assert(fixture.html.innerHTML.includes("&lt;Ready&gt;"), "Table markup did not encode <Ready>.");
      return { cells, encoded: true };
    });
    await check("dom-temporary-event-unregister", async () => {
      fixture.button.dispatchEvent(new FocusEvent("focus"));
      await bounded(new Promise(resolve => setTimeout(resolve, 30)));
      assert(fixture.status.textContent === "DOM event handler ready.", "Unregistered focus handler still ran.");
    });
    await check("dom-event-submit-prevent-default", async () => {
      fixture.input.value = "Browser Smoke";
      fixture.input.dispatchEvent(new Event("input", { bubbles: true }));
      let submitEvent;
      const observe = event => { submitEvent = event; };
      fixture.form.addEventListener("submit", observe);
      try {
        fixture.button.click();
        assert(submitEvent?.defaultPrevented === true, "Submit handler did not prevent default navigation.");
        await waitFor(() => fixture.status.textContent === "Hello Browser Smoke from a PowerShell DOM event.",
          "Submit handler did not update the status.");
      } finally { fixture.form.removeEventListener("submit", observe); }
      return { defaultPrevented: true, text: fixture.status.textContent };
    });
    await check("dom-button-reset", async () => {
      await waitFor(() => fixture.button.disabled === false, "Submit handler did not re-enable the button.");
    });
    await check("dom-storage-binding", () => {
      assert(localStorage.getItem(storageKeys[0]) === "Browser Smoke", "Input binding did not persist Browser Smoke.");
    });
    await check("dom-temporary-binding-unregister", () => {
      assert(localStorage.getItem(storageKeys[1]) === null, "Unregistered temporary binding still persisted the input.");
    });

    await check("api-text", async () => {
      const text = await bounded(api.executePowerShell("'API text smoke'; Write-Host 'Host text'; 2 + 3"));
      assert(text === "API text smoke\nHost text\n5", `Unexpected text API output: ${text}`);
    });
    await check("api-stream-records", async () => {
      const result = await bounded(api.executePowerShellResult(`
'Smoke output <safe>'
Write-Host 'Smoke host'
$DebugPreference = 'Continue'
$InformationPreference = 'Continue'
$VerbosePreference = 'Continue'
Write-Debug 'Smoke debug'
Write-Information 'Smoke information'
Write-Progress -Activity 'SmokeProgress' -Status 'Halfway' -PercentComplete 50
Write-Verbose 'Smoke verbose'
Write-Warning 'Smoke warning'
Write-Error 'Smoke error'
`));
      // Write-Host currently follows the runtime's text-compatible Output behavior.
      const streams = ["Output", "Output", "Debug", "Information", "Progress", "Verbose", "Warning", "Error"];
      assert(typeof result.text === "string" && Array.isArray(result.records), "Structured result has the wrong shape.");
      assert(result.records.length === streams.length, `Expected eight records, received ${result.records.length}.`);
      assert(result.records.map(record => record.stream).join("|") === streams.join("|"), "Stream ordering changed.");
      const texts = ["Smoke output <safe>", "Smoke host", "Smoke debug", "Smoke information",
        "SmokeProgress - Halfway - 50%", "Smoke verbose", "Smoke warning", "Smoke error"];
      assert(JSON.stringify(result.records.map(record => record.text)) === JSON.stringify(texts), "Stream text changed.");
      assert(result.text === texts.map((text, index) => streams[index] === "Output" ? text : `[${streams[index]}] ${text}`).join("\n"),
        "Joined stream output did not match the structured records.");
      const output = addNode("pre");
      output.hidden = true;
      api.renderPowerShellResult(result, output);
      assert(output.textContent.includes("Smoke output <safe>") && !output.querySelector("safe"),
        "Rendering did not preserve literal output text.");
      for (const stream of streams.slice(2)) {
        assert(output.querySelector(`.pswasm-stream-${stream.toLowerCase()}`), `Missing rendered ${stream} stream.`);
      }
      return { streams };
    });
    await check("api-environment", async () => {
      const text = await bounded(api.executePowerShell("$env:PSWASM_SMOKE", { environment: { PSWASM_SMOKE: "LocalOnly" } }));
      assert(text === "LocalOnly", `Environment injection failed: ${text}`);
    });
    await check("api-session-persistence", async () => {
      apiSession = await createSession({ PSWASM_SMOKE_SESSION: "SessionOnly" });
      assert(typeof apiSession.id === "string" && apiSession.id.length > 0, "Session id is missing.");
      assert(await bounded(apiSession.execute("$SmokeValue = 7; function Get-SmokeValue { $SmokeValue }; 'first'")) === "first",
        "Initial session execution failed.");
      const text = await bounded(api.executePowerShellSession(apiSession,
        "$SmokeValue += 2; Get-SmokeValue; $env:PSWASM_SMOKE_SESSION"));
      assert(text === "9\nSessionOnly", `Session state did not persist: ${text}`);
    });
    await check("api-session-output-reset", async () => {
      const result = await bounded(api.executePowerShellSessionResult(apiSession.id, "Get-SmokeValue"));
      assert(result.text === "9" && result.records.length === 1 && result.records[0].stream === "Output",
        "Session output accumulated earlier records.");
      assert(await bounded(api.executePowerShell("Get-SmokeValue", { session: apiSession })) === "9",
        "Session option routing failed.");
      assert((await bounded(apiSession.executeResult("Get-SmokeValue"))).text === "9", "Session executeResult helper failed.");
    });
    await check("api-session-disposal", async () => {
      assert(await bounded(apiSession.dispose()) === true, "First disposal did not return true.");
      sessions.delete(apiSession);
      assert(await bounded(api.disposePowerShellSession(apiSession.id)) === false, "Repeated disposal did not return false.");
      let rejected = false;
      try { await bounded(apiSession.execute("'must not execute'")); }
      catch (error) {
        rejected = /does not exist/i.test(errorText(error));
        assert(rejected, `Disposed session failed unexpectedly: ${errorText(error)}`);
      }
      assert(rejected, "Executing a disposed session unexpectedly succeeded.");
    });
    await check("external-script-order-shared-session", async () => {
      const host = addNode("div");
      host.hidden = true;
      const output = addNode("pre", host);
      const addScript = (text, source) => {
        const script = addNode("script", host);
        script.type = "text/pswasm-smoke";
        script.className = "pswasm-smoke-loader-script";
        if (source) script.setAttribute("src", source);
        else script.textContent = text;
      };
      addScript("$ScriptOrder = 'first'; function Get-SmokeOrder { $ScriptOrder }; 'inline first'");
      addScript("", "/sample.ps1");
      addScript("$ScriptOrder += ':last'; Get-SmokeOrder");
      await bounded(api.runPowerShellScripts({ selector: ".pswasm-smoke-loader-script", output }));
      const text = output.textContent, order = ["inline first", "PSWasm external PowerShell script", "Loaded from sample.ps1", "first:last"];
      assert(text === order.join("\n"), `External script order/shared session output changed: ${text}`);
      assert(!output.querySelector(".pswasm-stream-error"), `Script loader returned an error: ${text}`);
      return { order };
    });
    await check("browser-errors", async () => {
      await bounded(new Promise(resolve => setTimeout(resolve, 20)));
      assert(report.errors.length === 0, `Browser errors: ${report.errors.join("; ")}`);
    });
  } catch (error) {
    report.errors.push(errorText(error));
  } finally {
    stopped = true;
    const cleanup = { name: "cleanup", status: "passed" }, cleanupErrors = [];
    report.checks.push(cleanup);
    // The public API has no cancellation contract. Drain work before restoring shared state.
    let drainTimer;
    try {
      await Promise.race([Promise.allSettled([...pendingOperations]), new Promise(resolve => {
        drainTimer = setTimeout(resolve, 2_000);
      })]);
    } finally { clearTimeout(drainTimer); }
    if (pendingOperations.size > 0) {
      cleanupErrors.push("Runtime work remains pending after timeout; discard this fixture tab.");
      cleanup.details = { pendingOperations: pendingOperations.size, requiresTabClose: true };
    }
    const attempt = async action => {
      let timer;
      try {
        await Promise.race([Promise.resolve().then(action), new Promise((_, reject) => {
          timer = setTimeout(() => reject(new Error("Cleanup operation timed out.")), 2_000);
        })]);
      } catch (error) { cleanupErrors.push(errorText(error)); }
      finally { clearTimeout(timer); }
    };
    // Track only registrations created by this fixture, including partial initialization.
    if (api && domSession && pendingOperations.size === 0) {
      const cleanRuntime = async script => {
        const result = await domSession.executeResult(script);
        assert(!result.records.some(record => record.stream === "Error"), `Runtime cleanup failed: ${result.text}`);
      };
      for (const id of events) await attempt(() => cleanRuntime(`Unregister-DomEvent ${id}`));
      for (const id of bindings) await attempt(() => cleanRuntime(`Unregister-DomStorageBinding ${id}`));
      await attempt(() => cleanRuntime("if ($Dom) { Remove-DomSession $Dom }"));
    }
    for (const id of events) await attempt(() => globalThis.pswasmDom.unregisterEvent(id));
    for (const id of bindings) await attempt(() => globalThis.pswasmDom.unregisterStorageBinding(id));
    for (const session of sessions) await attempt(() => session.dispose());
    for (const { bridge, method, original, hook } of bridgeHooks) {
      if (bridge[method] === hook) bridge[method] = original;
    }
    for (const node of nodes.reverse()) node.remove();
    if (!originalStyle) document.getElementById("pswasm-default-styles")?.remove();
    if (fixture?.snapshot) {
      fixture.input.value = fixture.snapshot.value;
      fixture.button.disabled = fixture.snapshot.disabled;
      fixture.status.textContent = fixture.snapshot.status;
      fixture.html.innerHTML = fixture.snapshot.html;
    }
    for (const [key, value] of storedValues) {
      await attempt(() => value === null ? localStorage.removeItem(key) : localStorage.setItem(key, value));
    }
    if (originalAutoRun) Object.defineProperty(globalThis, "pswasmDisableAutoRun", originalAutoRun);
    else delete globalThis.pswasmDisableAutoRun;
    window.removeEventListener("error", captureError, true);
    window.removeEventListener("unhandledrejection", captureRejection);
    if (console.error === consoleHook) console.error = originalConsoleError;
    if (cleanupErrors.length) {
      cleanup.status = "failed";
      cleanup.error = cleanupErrors.join("; ");
      report.errors.push(`Cleanup failed: ${cleanup.error}`);
    }
  }
  if (!expectedChecks || JSON.stringify(report.checks.map(check => check.name)) !== JSON.stringify(expectedChecks)) {
    report.errors.push("The required smoke assertion set did not complete.");
  }
  if (report.errors.length === 0 && report.checks.every(check => check.status === "passed")) report.status = "passed";
  report.finishedAt = new Date().toISOString();
  globalThis.pswasmBrowserSmokeResult = report;
  return report;
}

function errorText(error) {
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;
  try { return JSON.stringify(error) ?? String(error); } catch { return String(error); }
}
