import { dotnet } from "./_framework/dotnet.js";

let runtimeExports;

/**
 * Execute PowerShell text and return the traditional joined text output.
 * @param {string} script
 * @param {{ environment?: Record<string, string> }} [options]
 * @returns {Promise<string>}
 */
export async function executePowerShell(script, options = {}) {
  const result = await executePowerShellResult(script, options);
  return result.text;
}

/**
 * Execute PowerShell text and return stream-aware records for DOM rendering.
 * @param {string} script
 * @param {{ environment?: Record<string, string> }} [options]
 * @returns {Promise<{ text: string, records: Array<{ stream: string, text: string }> }>}
 */
export async function executePowerShellResult(script, options = {}) {
  const exports = await getRuntimeExports();
  const environment = JSON.stringify(options.environment ?? window.pswasmEnvironment ?? {});
  const json = await exports.PSWasm.BrowserHost.Interop.ExecuteJsonAsync(script, environment);
  return normalizeResult(JSON.parse(json));
}

/**
 * Find and execute browser script blocks. Defaults to script[type="pwsh"] and #output.
 * @param {{ environment?: Record<string, string>, output?: Element | string, selector?: string }} [options]
 * @returns {Promise<void>}
 */
export async function runPowerShellScripts(options = {}) {
  const output = resolveOutputElement(options.output);
  const scripts = Array.from(document.querySelectorAll(options.selector ?? 'script[type="pwsh"]'))
    .map(script => script.textContent.trim())
    .filter(Boolean);
  const results = [];

  try {
    for (const script of scripts) {
      results.push(await executePowerShellResult(script, options));
    }

    renderPowerShellResult(mergeResults(results), output);
  } catch (error) {
    console.error(error);
    renderPowerShellOutput("[Error] Runtime error. Check the browser console.", output);
  }
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
    .pswasm-stream-error { color: #dc2626; }
    .pswasm-stream-warning { color: #d97706; }
    .pswasm-stream-information { color: #16a34a; }
    .pswasm-stream-debug { color: #64748b; }
    .pswasm-stream-verbose { color: #2563eb; }
    .pswasm-stream-progress { color: #7c3aed; }
  `;
  document.head.append(style);
}

if (!window.pswasmDisableAutoRun) {
  await runPowerShellScripts();
}
