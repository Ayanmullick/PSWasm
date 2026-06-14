import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { dirname, extname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const scriptRoot = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptRoot, "..", "..");
const options = parseArgs(process.argv.slice(2));
const root = resolve(options.root ?? join(repoRoot, "artifacts", "BrowserHost", "wwwroot"));
const fixture = resolve(options.fixture ?? join(scriptRoot, "dom-smoke.html"));
const port = Number(options.port ?? 5010);
const debugPort = Number(options["debug-port"] ?? 9222);
const timeout = Number(options.timeout ?? 45_000);
const userDataDir = resolve(options["user-data-dir"] ?? join(repoRoot, "artifacts", "BrowserDomSmoke", "user-data"));

if (!globalThis.WebSocket) {
  throw new Error("This smoke test requires a Node.js runtime with global WebSocket support.");
}

if (!existsSync(join(root, "app.js"))) {
  throw new Error(`Published BrowserHost assets were not found under '${root}'. Run the publish step first.`);
}

if (!existsSync(fixture)) {
  throw new Error(`DOM smoke fixture was not found: '${fixture}'.`);
}

const server = await startStaticServer(root, fixture, port);

if (options.manual) {
  console.log("PSWasm Browser DOM smoke server is running.");
  console.log(`Open http://127.0.0.1:${server.port}/dom-smoke.html in Edge Tools or the VS Code integrated browser.`);
  console.log("Expected manual check: status starts as 'DOM event handler ready.'; changing the name and clicking the button updates the status text.");
  console.log("Press Ctrl+C to stop the server.");
  await waitForever();
  process.exit(0);
}

const browserPath = resolveBrowserPath(options.browser);
const browser = launchBrowser(browserPath, debugPort, userDataDir);

try {
  const cdp = await connectToBrowser(debugPort, timeout);
  const { sessionId } = await createPageSession(cdp, `http://127.0.0.1:${server.port}/dom-smoke.html`);
  const browserErrors = collectBrowserErrors(cdp);

  await waitFor(
    () => evaluate(cdp, sessionId, "document.querySelector('#dom-sample-status')?.textContent"),
    text => text === "DOM event handler ready.",
    timeout,
    "DOM event handler did not become ready.");

  await evaluate(cdp, sessionId, `
(() => {
  const input = document.querySelector('#dom-sample-name');
  input.value = 'Browser Smoke';
  input.dispatchEvent(new Event('input', { bubbles: true }));
  document.querySelector('#dom-sample-button').click();
})()
`);

  const expected = "Hello Browser Smoke from a PowerShell DOM event.";
  await waitFor(
    () => evaluate(cdp, sessionId, "document.querySelector('#dom-sample-status')?.textContent"),
    text => text === expected,
    timeout,
    `DOM event did not update status text to '${expected}'.`);

  if (browserErrors.length > 0) {
    throw new Error(`Browser console errors were reported:${SystemLineBreak}${browserErrors.join(SystemLineBreak)}`);
  }

  console.log("PASS browser DOM smoke");
} finally {
  browser.kill();
  await closeServer(server);
}

function parseArgs(args) {
  const parsed = {};
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (!arg.startsWith("--")) {
      continue;
    }

    const key = arg.slice(2);
    const next = args[index + 1];
    if (next === undefined || next.startsWith("--")) {
      parsed[key] = true;
      continue;
    }

    parsed[key] = next;
    index += 1;
  }

  return parsed;
}

function resolveBrowserPath(explicitPath) {
  if (explicitPath) {
    return explicitPath;
  }

  if (process.env.PSWASM_BROWSER) {
    return process.env.PSWASM_BROWSER;
  }

  const candidates = process.platform === "win32"
    ? [
        "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
        "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
        "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
        "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe"
      ]
    : process.platform === "darwin"
      ? [
          "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
          "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
          "/Applications/Chromium.app/Contents/MacOS/Chromium"
        ]
      : [
          "/usr/bin/microsoft-edge",
          "/usr/bin/google-chrome",
          "/usr/bin/chromium",
          "/usr/bin/chromium-browser"
        ];

  const candidate = candidates.find(existsSync);
  if (!candidate) {
    throw new Error("No supported browser was found. Set --browser or PSWASM_BROWSER to an Edge, Chrome, or Chromium executable.");
  }

  return candidate;
}

function launchBrowser(browserPath, remoteDebuggingPort, profileDir) {
  rmSync(profileDir, { recursive: true, force: true });
  mkdirSync(profileDir, { recursive: true });

  const args = [
    "--headless=new",
    `--remote-debugging-port=${remoteDebuggingPort}`,
    `--user-data-dir=${profileDir}`,
    "--no-first-run",
    "--disable-background-networking",
    "--disable-default-apps",
    "--disable-extensions",
    "--disable-gpu",
    "about:blank"
  ];

  return spawn(browserPath, args, { stdio: "ignore" });
}

async function startStaticServer(staticRoot, fixturePath, requestedPort) {
  const fixtureHtml = readFileSync(fixturePath);
  const server = createServer((request, response) => {
    try {
      const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`);
      if (url.pathname === "/dom-smoke.html") {
        send(response, 200, "text/html; charset=utf-8", fixtureHtml);
        return;
      }

      const filePath = resolve(staticRoot, "." + decodeURIComponent(url.pathname));
      if (!isInside(staticRoot, filePath) || !existsSync(filePath)) {
        send(response, 404, "text/plain; charset=utf-8", "Not Found");
        return;
      }

      send(response, 200, contentType(filePath), readFileSync(filePath));
    } catch (error) {
      send(response, 500, "text/plain; charset=utf-8", String(error));
    }
  });

  await new Promise((resolveListen, rejectListen) => {
    server.once("error", rejectListen);
    server.listen(requestedPort, "127.0.0.1", resolveListen);
  });

  return { server, port: server.address().port };
}

function closeServer({ server }) {
  return new Promise(resolveClose => server.close(resolveClose));
}

function isInside(parent, child) {
  const normalizedParent = resolve(parent);
  const normalizedChild = resolve(child);
  return normalizedChild === normalizedParent || normalizedChild.startsWith(normalizedParent + sep);
}

function contentType(filePath) {
  return {
    ".css": "text/css; charset=utf-8",
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".wasm": "application/wasm"
  }[extname(filePath).toLowerCase()] ?? "application/octet-stream";
}

function send(response, statusCode, type, body) {
  response.writeHead(statusCode, { "Content-Type": type });
  response.end(body);
}

async function connectToBrowser(remoteDebuggingPort, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${remoteDebuggingPort}/json/version`);
      if (response.ok) {
        const info = await response.json();
        return await CdpClient.connect(info.webSocketDebuggerUrl);
      }
    } catch {
    }

    await delay(100);
  }

  throw new Error("Timed out waiting for browser DevTools endpoint.");
}

async function createPageSession(cdp, url) {
  const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
  await cdp.send("Runtime.enable", {}, sessionId);
  await cdp.send("Log.enable", {}, sessionId);
  await cdp.send("Page.enable", {}, sessionId);
  await cdp.send("Page.navigate", { url }, sessionId);
  await waitFor(
    () => evaluate(cdp, sessionId, "document.readyState"),
    state => state === "interactive" || state === "complete",
    timeout,
    "Page did not load.");
  return { targetId, sessionId };
}

function collectBrowserErrors(cdp) {
  const errors = [];
  cdp.on("Runtime.exceptionThrown", event => {
    errors.push(event.params?.exceptionDetails?.text ?? "Runtime exception");
  });
  cdp.on("Runtime.consoleAPICalled", event => {
    if (event.params?.type === "error") {
      errors.push((event.params.args ?? []).map(formatRemoteValue).join(" "));
    }
  });
  cdp.on("Log.entryAdded", event => {
    if (event.params?.entry?.level === "error") {
      errors.push(event.params.entry.text);
    }
  });
  return errors;
}

async function evaluate(cdp, sessionId, expression) {
  const response = await cdp.send("Runtime.evaluate", {
    expression,
    awaitPromise: true,
    returnByValue: true
  }, sessionId);

  if (response.exceptionDetails) {
    throw new Error(response.exceptionDetails.text ?? "Runtime.evaluate failed.");
  }

  return response.result?.value;
}

function formatRemoteValue(value) {
  if (value?.value !== undefined) {
    return String(value.value);
  }

  return value?.description ?? value?.type ?? "";
}

async function waitFor(getValue, predicate, timeoutMs, failureMessage) {
  const deadline = Date.now() + timeoutMs;
  let lastValue;
  while (Date.now() < deadline) {
    lastValue = await getValue();
    if (predicate(lastValue)) {
      return lastValue;
    }

    await delay(100);
  }

  throw new Error(`${failureMessage} Last value: ${JSON.stringify(lastValue)}`);
}

function delay(ms) {
  return new Promise(resolveDelay => setTimeout(resolveDelay, ms));
}

function waitForever() {
  return new Promise(resolveWait => {
    const stop = async () => {
      await closeServer(server);
      resolveWait();
    };
    process.once("SIGINT", stop);
    process.once("SIGTERM", stop);
  });
}

const SystemLineBreak = "\n";

class CdpClient {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 1;
    this.pending = new Map();
    this.handlers = new Map();

    socket.addEventListener("message", event => this.handleMessage(event.data));
  }

  static connect(url) {
    return new Promise((resolveConnect, rejectConnect) => {
      const socket = new WebSocket(url);
      socket.addEventListener("open", () => resolveConnect(new CdpClient(socket)));
      socket.addEventListener("error", () => rejectConnect(new Error("Failed to connect to browser WebSocket.")));
    });
  }

  send(method, params = {}, sessionId = undefined) {
    const id = this.nextId++;
    const message = sessionId === undefined ? { id, method, params } : { id, method, params, sessionId };
    this.socket.send(JSON.stringify(message));
    return new Promise((resolveSend, rejectSend) => {
      this.pending.set(id, { resolve: resolveSend, reject: rejectSend });
    });
  }

  on(method, handler) {
    const handlers = this.handlers.get(method) ?? [];
    handlers.push(handler);
    this.handlers.set(method, handlers);
  }

  handleMessage(data) {
    const message = JSON.parse(data);
    if (message.id !== undefined) {
      const pending = this.pending.get(message.id);
      if (!pending) {
        return;
      }

      this.pending.delete(message.id);
      if (message.error) {
        pending.reject(new Error(message.error.message));
      } else {
        pending.resolve(message.result ?? {});
      }

      return;
    }

    for (const handler of this.handlers.get(message.method) ?? []) {
      handler(message);
    }
  }
}
