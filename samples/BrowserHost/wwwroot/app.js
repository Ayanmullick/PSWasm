import { dotnet } from "./_framework/dotnet.js";

let runtimeExports;
const domEventRegistrations = new Map();
const domStorageRegistrations = new Map();
const azureAuthState = {
  app: undefined,
  configKey: "",
  redirectPromise: undefined,
  msalLoad: undefined
};
const azureAuthStorageKey = "pswasm.azureAuth";

globalThis.pswasmDom = {
  getText: selector => resolveDomElement(selector).textContent ?? "",
  setText: (selector, text) => {
    resolveDomElement(selector).textContent = text ?? "";
  },
  setHtml: (selector, html) => {
    resolveDomElement(selector).innerHTML = html ?? "";
  },
  getValue: selector => {
    const element = resolveDomElement(selector);
    return getElementValue(element);
  },
  setProperty: (selector, propertyName, valueJson) => {
    const element = resolveDomElement(selector);
    element[normalizeDomPropertyName(propertyName)] = JSON.parse(valueJson);
  },
  getProperty: (selector, propertyName) => {
    const element = resolveDomElement(selector);
    return serializeDomJsonValue(element[normalizeDomPropertyName(propertyName)]);
  },
  getStorageItem: (storage, key) => {
    const item = resolveDomStorage(storage).getItem(key);
    return JSON.stringify({ exists: item !== null, value: item ?? "" });
  },
  setStorageItem: (storage, key, value) => {
    resolveDomStorage(storage).setItem(key, value ?? "");
  },
  removeStorageItem: (storage, key) => {
    resolveDomStorage(storage).removeItem(key);
  },
  clearStorage: storage => {
    resolveDomStorage(storage).clear();
  },
  registerStorageBinding: (registrationId, storage, mapJson, eventName, propertyName) => {
    const key = `${registrationId}`;
    const previous = domStorageRegistrations.get(key);
    if (previous) {
      for (const binding of previous.bindings) {
        binding.element.removeEventListener(binding.eventName, binding.handler);
      }
    }

    const storageArea = resolveDomStorage(storage);
    const normalizedEventName = normalizeDomEventName(eventName || "Input");
    const normalizedPropertyName = normalizeDomPropertyName(propertyName || "Value");
    const map = JSON.parse(mapJson);
    const bindings = [];
    for (const [selector, storageKey] of Object.entries(map)) {
      const element = resolveDomElement(selector);
      const storedValue = storageArea.getItem(storageKey);
      if (storedValue !== null) {
        setElementBoundValue(element, normalizedPropertyName, storedValue);
      }

      const handler = () => {
        storageArea.setItem(storageKey, getElementBoundValue(element, normalizedPropertyName));
      };
      element.addEventListener(normalizedEventName, handler);
      bindings.push({ element, eventName: normalizedEventName, handler });
    }

    domStorageRegistrations.set(key, { storage, map, bindings });
  },
  registerEvent: (registrationId, selector, eventName, preventDefault) => {
    const key = `${registrationId}`;
    const previous = domEventRegistrations.get(key);
    if (previous) {
      previous.element.removeEventListener(previous.eventName, previous.handler);
    }

    const element = resolveDomElement(selector);
    const normalizedEventName = normalizeDomEventName(eventName);
    const handler = async event => {
      if (preventDefault) {
        event.preventDefault();
      }

      try {
        const exports = await getRuntimeExports();
        const json = await exports.PSWasm.BrowserHost.Interop.InvokeDomEventJsonAsync(
          registrationId,
          JSON.stringify(createDomEventData(event)));
        const result = normalizeResult(JSON.parse(json));
        if (result.text) {
          console.log(result.text);
        }
      } catch (error) {
        console.error(error);
      }
    };

    element.addEventListener(normalizedEventName, handler);
    domEventRegistrations.set(key, { element, eventName: normalizedEventName, handler });
  },
  unregisterEvent: registrationId => {
    const key = `${registrationId}`;
    const previous = domEventRegistrations.get(key);
    if (!previous) {
      return;
    }

    previous.element.removeEventListener(previous.eventName, previous.handler);
    domEventRegistrations.delete(key);
  },
  unregisterStorageBinding: registrationId => {
    const key = `${registrationId}`;
    const previous = domStorageRegistrations.get(key);
    if (!previous) {
      return;
    }

    for (const binding of previous.bindings) {
      binding.element.removeEventListener(binding.eventName, binding.handler);
    }

    domStorageRegistrations.delete(key);
  }
};

globalThis.pswasmAzureAuth = {
  connect: async optionsJson => JSON.stringify(await connectAzureAuth(parseAzureAuthOptions(optionsJson))),
  getContext: async () => JSON.stringify(await getAzureAuthContext()),
  getAccessToken: async optionsJson => JSON.stringify(await getAzureAccessToken(parseAzureAuthOptions(optionsJson))),
  disconnect: async () => JSON.stringify(await disconnectAzureAuth())
};

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
 * Set data-pswasm-output="none" on DOM-only script blocks to suppress the success output block.
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
      const outputMode = getPowerShellScriptOutputMode(script);
      let output = outputMode === "none" ? undefined : getOrCreateScriptOutputElement(script);
      const ensureOutput = () => output ??= getOrCreateScriptOutputElement(script);

      try {
        const result = await executePowerShellResult(await getPowerShellScriptText(script), executeOptions);
        if (outputMode !== "none" || hasErrorRecord(result)) {
          renderPowerShellResult(result, ensureOutput());
        }
      } catch (error) {
        console.error(error);
        renderPowerShellOutput("[Error] Runtime error. Check the browser console.", ensureOutput());
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

function getPowerShellScriptOutputMode(script) {
  return script.getAttribute("data-pswasm-output")?.trim().toLowerCase() ?? "auto";
}

function hasErrorRecord(result) {
  return (result.records ?? []).some(record => (record.stream ?? "").toLowerCase() === "error");
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
  const records = result.records ?? [];
  installDefaultStyles();
  output.textContent = "";
  setGeneratedOutputVisibility(output, records.length > 0);

  for (const [index, record] of records.entries()) {
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
  const lines = text.split(/\r?\n/);
  installDefaultStyles();
  output.textContent = "";
  setGeneratedOutputVisibility(output, text.length > 0);

  for (const [index, line] of lines.entries()) {
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

function setGeneratedOutputVisibility(output, hasContent) {
  if (output.dataset?.pswasmGenerated === "true") {
    output.hidden = !hasContent;
  }
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

function resolveDomElement(selector) {
  if (selector === "document") {
    return document.documentElement;
  }

  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`No DOM element matches selector '${selector}'.`);
  }

  return element;
}

function getElementValue(element) {
  if ("value" in element) {
    return element.value ?? "";
  }

  return element.textContent ?? "";
}

function getElementBoundValue(element, propertyName) {
  if (propertyName in element) {
    const value = element[propertyName];
    return value === undefined || value === null ? "" : String(value);
  }

  return element.getAttribute(propertyName) ?? "";
}

function setElementBoundValue(element, propertyName, value) {
  if (propertyName in element) {
    if (typeof element[propertyName] === "boolean") {
      element[propertyName] = value === "true";
      return;
    }

    element[propertyName] = value ?? "";
    return;
  }

  element.setAttribute(propertyName, value ?? "");
}

function serializeDomJsonValue(value) {
  const json = JSON.stringify(value ?? null);
  return json === undefined ? "null" : json;
}

function resolveDomStorage(storage) {
  if (typeof storage !== "string") {
    throw new Error("Storage must be Local or Session.");
  }

  if (storage.toLowerCase() === "local") {
    return localStorage;
  }

  if (storage.toLowerCase() === "session") {
    return sessionStorage;
  }

  throw new Error("Storage must be Local or Session.");
}

function normalizeDomPropertyName(propertyName) {
  if (!propertyName) {
    throw new Error("A DOM property name is required.");
  }

  return propertyName.charAt(0).toLowerCase() + propertyName.slice(1);
}

function normalizeDomEventName(eventName) {
  if (!eventName) {
    throw new Error("A DOM event name is required.");
  }

  return eventName.toLowerCase();
}

function createDomEventData(event) {
  return {
    type: event.type,
    target: describeElement(event.target),
    currentTarget: describeElement(event.currentTarget),
    value: event.target instanceof Element ? getElementValue(event.target) : "",
    values: collectNamedValues(event.currentTarget)
  };
}

function describeElement(element) {
  if (!(element instanceof Element)) {
    return {};
  }

  return {
    id: element.id ?? "",
    name: element.getAttribute("name") ?? "",
    tagName: element.tagName.toLowerCase()
  };
}

function collectNamedValues(root) {
  if (!(root instanceof Element)) {
    return {};
  }

  const values = {};
  for (const element of root.querySelectorAll("input, select, textarea")) {
    const key = element.getAttribute("name") || element.id;
    if (key) {
      values[key] = getElementValue(element);
    }
  }

  return values;
}

function parseAzureAuthOptions(optionsJson) {
  if (!optionsJson || optionsJson.trim() === "") {
    return {};
  }

  return JSON.parse(optionsJson);
}

async function connectAzureAuth(options) {
  const app = await getAzureMsalApp(options);
  const redirectResult = await handleAzureAuthRedirect(app);
  const account = redirectResult?.account || getActiveAzureAccount(app);
  if (account) {
    app.setActiveAccount(account);
    return createAzureAccountRecord(account, options.ClientId ?? options.clientId ?? "");
  }

  await app.loginRedirect({
    scopes: getAzureSignInScopes(options),
    redirectStartPage: location.href
  });
  throw new Error("Redirecting to Microsoft Entra sign-in.");
}

async function getAzureAuthContext() {
  const app = azureAuthState.app ?? await getStoredAzureMsalApp();
  if (!app) {
    return createEmptyAzureAccountRecord();
  }

  await handleAzureAuthRedirect(app);
  const account = getActiveAzureAccount(app);
  return account ? createAzureAccountRecord(account, app.pswasmClientId ?? "") : createEmptyAzureAccountRecord();
}

async function getAzureAccessToken(options) {
  const app = azureAuthState.app ?? await getStoredAzureMsalApp();
  if (!app) {
    throw new Error("Connect-AzAccount must be called before Get-AzAccessToken.");
  }

  await handleAzureAuthRedirect(app);
  const scopes = normalizeAzureScopes(options.Scopes ?? options.scopes);
  if (scopes.length === 0) {
    throw new Error("Get-AzAccessToken requires a ResourceUrl or Scope.");
  }

  let account = getActiveAzureAccount(app);
  if (!account) {
    await app.loginRedirect({ scopes, redirectStartPage: location.href });
    throw new Error("Redirecting to Microsoft Entra sign-in.");
  }

  try {
    const result = await app.acquireTokenSilent({ account, scopes });
    account = result.account || account;
    app.setActiveAccount(account);
    return createAzureAccessTokenRecord(result, account);
  } catch (error) {
    if (!isAzureInteractionRequired(error)) {
      throw error;
    }

    await app.acquireTokenRedirect({ account, scopes, redirectStartPage: location.href });
    throw new Error("Redirecting to acquire a user access token.");
  }
}

async function disconnectAzureAuth() {
  const app = azureAuthState.app;
  clearAzureAuthOptions();
  if (!app) {
    return createEmptyAzureAccountRecord();
  }

  const account = getActiveAzureAccount(app);
  if (!account) {
    return createEmptyAzureAccountRecord();
  }

  await app.logoutRedirect({ account, postLogoutRedirectUri: location.origin });
  throw new Error("Redirecting to Microsoft Entra sign-out.");
}

async function getAzureMsalApp(options) {
  await loadAzureMsal();

  const clientId = normalizeAzureText(options.ClientId ?? options.clientId);
  if (!clientId) {
    throw new Error("Connect-AzAccount requires -ClientId for browser public-client authentication.");
  }

  const tenant = normalizeAzureText(options.Tenant ?? options.tenant) || "organizations";
  const cacheLocation = getAzureAuthCacheLocation(options);
  const key = `${tenant}|${clientId}|${location.origin}|${cacheLocation}`;
  if (!azureAuthState.app || azureAuthState.configKey !== key) {
    azureAuthState.configKey = key;
    azureAuthState.redirectPromise = undefined;
    azureAuthState.app = new globalThis.msal.PublicClientApplication({
      auth: { clientId, authority: `https://login.microsoftonline.com/${tenant}`, redirectUri: location.origin },
      cache: { cacheLocation }
    });
    azureAuthState.app.pswasmClientId = clientId;
    if (typeof azureAuthState.app.initialize === "function") {
      await azureAuthState.app.initialize();
    }

    const account = azureAuthState.app.getAllAccounts()[0];
    if (account) {
      azureAuthState.app.setActiveAccount(account);
    }
  }

  persistAzureAuthOptions({ tenant, clientId, cacheLocation });
  return azureAuthState.app;
}

async function getStoredAzureMsalApp() {
  const options = readAzureAuthOptions();
  return options ? getAzureMsalApp(options) : undefined;
}

async function handleAzureAuthRedirect(app) {
  if (!azureAuthState.redirectPromise) {
    azureAuthState.redirectPromise = app.handleRedirectPromise().then(result => {
      if (result?.account) {
        app.setActiveAccount(result.account);
      }

      return result;
    });
  }

  return azureAuthState.redirectPromise;
}

async function loadAzureMsal() {
  if (globalThis.msal) {
    return;
  }

  if (azureAuthState.msalLoad) {
    return azureAuthState.msalLoad;
  }

  const sources = [
    "https://alcdn.msauth.net/browser/2.38.3/js/msal-browser.min.js",
    "https://cdn.jsdelivr.net/npm/@azure/msal-browser@2.38.3/lib/msal-browser.min.js",
    "https://unpkg.com/@azure/msal-browser@2.38.3/lib/msal-browser.min.js"
  ];

  azureAuthState.msalLoad = (async () => {
    const failures = [];
    for (const source of sources) {
      try {
        await loadScript(source);
        if (globalThis.msal) {
          return;
        }

        failures.push(`${source}: loaded but window.msal was not created`);
      } catch (error) {
        failures.push(`${source}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    throw new Error(`MSAL.js did not load.\n${failures.join("\n")}`);
  })();

  return azureAuthState.msalLoad;
}

function loadScript(source) {
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    const timer = setTimeout(() => {
      script.remove();
      reject(new Error("timed out"));
    }, 10000);

    script.src = source;
    script.async = true;
    script.onload = () => {
      clearTimeout(timer);
      resolve();
    };
    script.onerror = () => {
      clearTimeout(timer);
      script.remove();
      reject(new Error("failed to load"));
    };
    document.head.appendChild(script);
  });
}

function persistAzureAuthOptions(options) {
  try {
    localStorage.setItem(azureAuthStorageKey, JSON.stringify({
      Tenant: options.tenant,
      ClientId: options.clientId,
      CacheLocation: options.cacheLocation
    }));
  } catch {
    // Browser storage can be disabled; the current in-memory runtime still works.
  }
}

function readAzureAuthOptions() {
  try {
    const json = localStorage.getItem(azureAuthStorageKey);
    if (!json) {
      return undefined;
    }

    const options = JSON.parse(json);
    return normalizeAzureText(options.ClientId ?? options.clientId) ? options : undefined;
  } catch {
    clearAzureAuthOptions();
    return undefined;
  }
}

function clearAzureAuthOptions() {
  try {
    localStorage.removeItem(azureAuthStorageKey);
  } catch {
    // Ignore storage cleanup failures; sign-out still proceeds for the current MSAL app.
  }
}

function getAzureSignInScopes(options) {
  const scopes = normalizeAzureScopes(options.Scopes ?? options.scopes);
  return scopes.length > 0 ? scopes : ["openid", "profile"];
}

function normalizeAzureScopes(scopes) {
  if (!Array.isArray(scopes)) {
    return [];
  }

  return scopes.map(scope => normalizeAzureText(scope)).filter(Boolean);
}

function normalizeAzureText(value) {
  return typeof value === "string" ? value.trim() : "";
}

function getAzureAuthCacheLocation(options) {
  const value = normalizeAzureText(options.CacheLocation ?? options.cacheLocation);
  return value === "sessionStorage" ? "sessionStorage" : "localStorage";
}

function getActiveAzureAccount(app) {
  return app.getActiveAccount() || app.getAllAccounts()[0];
}

function isAzureInteractionRequired(error) {
  return error instanceof globalThis.msal.InteractionRequiredAuthError ||
    ["interaction_required", "login_required", "consent_required"].includes(error?.errorCode);
}

function createAzureAccountRecord(account, clientId) {
  return {
    Authenticated: true,
    UserName: account.username ?? "",
    Name: account.name ?? "",
    TenantId: account.tenantId ?? account.idTokenClaims?.tid ?? "",
    UserId: account.localAccountId ?? account.homeAccountId ?? "",
    ClientId: clientId
  };
}

function createEmptyAzureAccountRecord() {
  return {
    Authenticated: false,
    UserName: "",
    Name: "",
    TenantId: "",
    UserId: "",
    ClientId: ""
  };
}

function createAzureAccessTokenRecord(result, account) {
  return {
    AccessToken: result.accessToken ?? "",
    ExpiresOn: result.expiresOn instanceof Date ? result.expiresOn.toISOString() : "",
    TenantId: result.tenantId ?? account.tenantId ?? account.idTokenClaims?.tid ?? "",
    UserId: account.localAccountId ?? account.homeAccountId ?? "",
    Account: account.username ?? account.name ?? "",
    TokenType: result.tokenType ?? "Bearer"
  };
}

if (!globalThis.pswasmDisableAutoRun) {
  await runPowerShellScripts();
}
