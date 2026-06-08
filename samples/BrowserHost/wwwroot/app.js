import { dotnet } from "./_framework/dotnet.js";

const output = document.getElementById("output") ?? document.body.appendChild(document.createElement("pre"));
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

  output.textContent = results.filter(Boolean).join("\n");
} catch (error) {
  console.error(error);
  output.textContent = "Runtime error. Check the browser console.";
}
