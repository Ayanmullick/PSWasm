import { dotnet } from "./_framework/dotnet.js";

let runtimeExports;

/**
 * Execute PowerShell text and return the traditional joined text output.
 * @param {string} script
 * @param {{ environment?: Record<string, string>, session?: string | { id: string } }} [options]
 * @returns {Promise<string>}
 */
export async function executePowerShell(script, options = {}) {
  const result = await executePowerShellResult(script, options);
  return result.text;
}

/**
 * Execute PowerShell text and return stream-aware records for DOM rendering.
 * @param {string} script
 * @param {{ environment?: Record<string, string>, session?: string | { id: string } }} [options]
 * @returns {Promise<{ text: string, records: Array<{ stream: string, text: string }> }>}
 */
export async function executePowerShellResult(script, options = {}) {
  if (options.session !== undefined) {
    return executePowerShellSessionResult(options.session, script);
  }

  const exports = await getRuntimeExports();
  const json = await exports.PSWasm.BrowserHost.Interop.ExecuteJsonAsync(script, getEnvironmentJson(options));
  return normalizeResult(JSON.parse(json));
}

/**
 * Create a reusable browser PowerShell session. Variables and functions persist until dispose().
 * @param {{ environment?: Record<string, string> }} [options]
 * @returns {Promise<{ id: string, execute(script: string): Promise<string>, executeResult(script: string): Promise<{ text: string, records: Array<{ stream: string, text: string }> }>, dispose(): Promise<boolean> }>}
 */
export async function createPowerShellSession(options = {}) {
  const exports = await getRuntimeExports();
  const id = exports.PSWasm.BrowserHost.Interop.CreateSession(getEnvironmentJson(options));

  return {
    id,
    execute: script => executePowerShellSession(id, script),
    executeResult: script => executePowerShellSessionResult(id, script),
    dispose: () => disposePowerShellSession(id)
  };
}

/**
 * Execute PowerShell text in an existing session and return joined text output.
 * @param {string | { id: string }} session
 * @param {string} script
 * @returns {Promise<string>}
 */
export async function executePowerShellSession(session, script) {
  const result = await executePowerShellSessionResult(session, script);
  return result.text;
}

/**
 * Execute PowerShell text in an existing session and return stream-aware records.
 * @param {string | { id: string }} session
 * @param {string} script
 * @returns {Promise<{ text: string, records: Array<{ stream: string, text: string }> }>}
 */
export async function executePowerShellSessionResult(session, script) {
  const exports = await getRuntimeExports();
  const json = await exports.PSWasm.BrowserHost.Interop.ExecuteSessionJsonAsync(getSessionId(session), script);
  return normalizeResult(JSON.parse(json));
}

/**
 * Dispose an existing browser PowerShell session.
 * @param {string | { id: string }} session
 * @returns {Promise<boolean>}
 */
export async function disposePowerShellSession(session) {
  const exports = await getRuntimeExports();
  return exports.PSWasm.BrowserHost.Interop.DisposeSession(getSessionId(session));
}

/**
 * Find and execute browser script blocks or external PowerShell scripts. Auto-run creates one output block per script.
 * Script blocks share one PowerShell session by default. Pass session: false to isolate blocks.
 * Pass options.output to render all scripts into one combined output target.
 * @param {{ environment?: Record<string, string>, output?: Element | string, selector?: string, session?: boolean | string | { id: string } }} [options]
 * @returns {Promise<void>}
 */
export async function runPowerShellScripts(options = {}) {
  const scripts = Array.from(document.querySelectorAll(options.selector ?? 'script[type="pwsh"]'))
    .filter(hasPowerShellScriptText);
  const shouldCreateSession = options.session === undefined || options.session === true;
  const ownedSession = shouldCreateSession ? await createPowerShellSession(options) : undefined;
  const session = ownedSession ?? (options.session === false ? undefined : options.session);
  const executeOptions = getPowerShellExecutionOptions(options, session);

  try {
    if (options.output !== undefined) {
      const output = resolveOutputElement(options.output);
      const results = [];

      for (const script of scripts) {
        results.push(await executePowerShellResult(await getPowerShellScriptText(script), executeOptions));
      }

      renderPowerShellResult(mergeResults(results), output);
      return;
    }

    for (const script of scripts) {
      const output = getOrCreateScriptOutputElement(script);

      try {
        renderPowerShellResult(await executePowerShellResult(await getPowerShellScriptText(script), executeOptions), output);
      } catch (error) {
        console.error(error);
        renderPowerShellOutput("[Error] Runtime error. Check the browser console.", output);
      }
    }
  } catch (error) {
    console.error(error);
    renderPowerShellOutput("[Error] Runtime error. Check the browser console.", options.output);
  } finally {
    await ownedSession?.dispose();
  }
}

function hasPowerShellScriptText(script) {
  return getPowerShellScriptSource(script) !== "" || script.textContent.trim() !== "";
}

async function getPowerShellScriptText(script) {
  const source = getPowerShellScriptSource(script);
  if (source === "") {
    return script.textContent.trim();
  }

  const response = await fetch(new URL(source, document.baseURI));
  if (!response.ok) {
    throw new Error(`Failed to load PowerShell script '${source}' (${response.status} ${response.statusText}).`);
  }

  return (await response.text()).trim();
}

function getPowerShellScriptSource(script) {
  return script.getAttribute("src")?.trim() ?? "";
}

function getPowerShellExecutionOptions(options, session) {
  if (session !== undefined) {
    return { ...options, session };
  }

  const { session: _session, ...executionOptions } = options;
  return executionOptions;
}

/**
 * Render structured records with stream-specific CSS classes.
 * @param {{ text?: string, records?: Array<{ stream?: string, text?: string }> }} result
 * @param {Element | string} [target]
 */
export function renderPowerShellResult(result, target = undefined) {
  const output = resolveOutputElement(target);
  installDefaultStyles();
  output.textContent = "";

  for (const [index, record] of (result.records ?? []).entries()) {
    if (index > 0) {
      output.append(document.createTextNode("\n"));
    }

    output.append(renderRecord(record));
  }
}

/**
 * Render legacy text output. Lines prefixed with [Warning], [Error], etc. are styled.
 * @param {string} text
 * @param {Element | string} [target]
 */
export function renderPowerShellOutput(text, target = undefined) {
  const output = resolveOutputElement(target);
  installDefaultStyles();
  output.textContent = "";

  for (const [index, line] of text.split(/\r?\n/).entries()) {
    if (index > 0) {
      output.append(document.createTextNode("\n"));
    }

    output.append(renderLine(line));
  }
}

function mergeResults(results) {
  const records = results.flatMap(result => result.records ?? []);
  return { text: records.map(formatRecordText).join("\n"), records };
}

function normalizeResult(result) {
  return {
    text: result.Text ?? result.text ?? "",
    records: (result.Records ?? result.records ?? []).map(record => ({
      stream: record.Stream ?? record.stream ?? "Output",
      text: record.Text ?? record.text ?? ""
    }))
  };
}

function getEnvironmentJson(options = {}) {
  return JSON.stringify(options.environment ?? globalThis.pswasmEnvironment ?? {});
}

function getSessionId(session) {
  if (typeof session === "string" && session.length > 0) {
    return session;
  }

  if (session?.id) {
    return session.id;
  }

  throw new Error("A PSWasm session id or session object is required.");
}

async function getRuntimeExports() {
  if (runtimeExports) {
    return runtimeExports;
  }

  const { getAssemblyExports, runMain } = await dotnet.withApplicationArgumentsFromQuery().create();
  await runMain();
  runtimeExports = await getAssemblyExports("PSWasm.BrowserHost");
  return runtimeExports;
}

function resolveOutputElement(target = undefined) {
  if (target instanceof Element) {
    return target;
  }

  if (typeof target === "string") {
    return document.querySelector(target) ?? document.body.appendChild(document.createElement("pre"));
  }

  return document.getElementById("output") ?? document.body.appendChild(document.createElement("pre"));
}

function getOrCreateScriptOutputElement(script) {
  const existing = script.nextElementSibling;
  if (existing?.classList.contains("pswasm-output") && existing.dataset.pswasmGenerated === "true") {
    return existing;
  }

  const output = document.createElement("pre");
  output.className = "pswasm-output";
  output.dataset.pswasmGenerated = "true";
  script.insertAdjacentElement("afterend", output);
  return output;
}

function renderRecord(record) {
  const element = document.createElement("span");
  const stream = record.stream ?? "Output";
  const text = record.text ?? "";

  if (stream.toLowerCase() === "output") {
    element.textContent = text;
    return element;
  }

  element.className = `pswasm-stream pswasm-stream-${stream.toLowerCase()}`;
  element.textContent = `[${stream}] ${text}`;
  return element;
}

function renderLine(line) {
  const stream = line.match(/^\[(Debug|Error|Information|Progress|Verbose|Warning)\]\s?(.*)$/);
  const element = document.createElement("span");

  if (!stream) {
    element.textContent = line;
    return element;
  }

  element.className = `pswasm-stream pswasm-stream-${stream[1].toLowerCase()}`;
  element.textContent = `[${stream[1]}] ${stream[2]}`;
  return element;
}

function formatRecordText(record) {
  const stream = record.stream ?? "Output";
  const text = record.text ?? "";
  return stream.toLowerCase() === "output" ? text : `[${stream}] ${text}`;
}

function installDefaultStyles() {
  if (document.getElementById("pswasm-default-styles")) {
    return;
  }

  const style = document.createElement("style");
  style.id = "pswasm-default-styles";
  style.textContent = `
    .pswasm-output { white-space: pre-wrap; }
    .pswasm-stream-error { color: #dc2626; }
    .pswasm-stream-warning { color: #d97706; }
    .pswasm-stream-information { color: #16a34a; }
    .pswasm-stream-debug { color: #64748b; }
    .pswasm-stream-verbose { color: #2563eb; }
    .pswasm-stream-progress { color: #7c3aed; }
  `;
  document.head.append(style);
}

if (!globalThis.pswasmDisableAutoRun) {
  await runPowerShellScripts();
}
