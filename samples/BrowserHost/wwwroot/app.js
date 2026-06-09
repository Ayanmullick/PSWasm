import { dotnet } from "./_framework/dotnet.js";

const output = document.getElementById("output") ?? document.body.appendChild(document.createElement("pre"));
installDefaultStyles();

const scripts = Array.from(document.querySelectorAll('script[type="pwsh"]'))
  .map(script => script.textContent.trim())
  .filter(Boolean);

try {
  const { getAssemblyExports, runMain } = await dotnet.withApplicationArgumentsFromQuery().create();
  await runMain();

  const exports = await getAssemblyExports("PSWasm.BrowserHost");
  const environment = JSON.stringify(window.pswasmEnvironment ?? {});
  const results = [];

  for (const script of scripts) {
    results.push(await exports.PSWasm.BrowserHost.Interop.ExecuteAsync(script, environment));
  }

  renderOutput(results.filter(Boolean).join("\n"));
} catch (error) {
  console.error(error);
  renderOutput("[Error] Runtime error. Check the browser console.");
}

function renderOutput(text) {
  output.textContent = "";

  for (const [index, line] of text.split(/\r?\n/).entries()) {
    if (index > 0) {
      output.append(document.createTextNode("\n"));
    }

    output.append(renderLine(line));
  }
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
