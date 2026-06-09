using PSWasm.Commands;
using PSWasm.Language;

namespace PSWasm;

public sealed class PowerShellWasmRuntime
{
    private readonly Dictionary<string, IPowerShellWasmCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly PowerShellWasmExecutionContext _executionContext;
    private readonly PowerShellWasmParser _parser = new();

    public PowerShellWasmRuntime(IDictionary<string, string>? environment = null)
    {
        _executionContext = new PowerShellWasmExecutionContext(environment);
        RegisterCommand("Get-Date", new GetDateCommand());
        RegisterCommand("Get-Time", new GetDateCommand(timeOnly: true));
        RegisterCommand("Get-TimeZone", new GetTimeZoneCommand());
        RegisterCommand("Write-Debug", new WriteStreamCommand("Debug", "Message"));
        RegisterCommand("Write-Error", new WriteStreamCommand("Error", "Message"));
        RegisterCommand("Write-Host", new WriteStreamCommand("Host", "Object", "Message"));
        RegisterCommand("Write-Information", new WriteStreamCommand("Information", "MessageData", "Message"));
        RegisterCommand("Write-Output", new WriteOutputCommand());
        RegisterCommand("Write-Progress", new WriteStreamCommand("Progress", "Activity"));
        RegisterCommand("Write-Verbose", new WriteStreamCommand("Verbose", "Message"));
        RegisterCommand("Write-Warning", new WriteStreamCommand("Warning", "Message"));
    }

    public void RegisterCommand(string name, IPowerShellWasmCommand command) =>
        _commands[name] = command;

    public ScriptAst Parse(string script) =>
        _parser.Parse(script);

    public async ValueTask<PowerShellWasmResult> ExecuteAsync(string script, CancellationToken cancellationToken = default)
    {
        var ast = Parse(script);
        var executor = new PowerShellWasmAstExecutor(_executionContext, _commands);
        await executor.ExecuteAsync(ast, cancellationToken);
        return new PowerShellWasmResult([.. _executionContext.Output]);
    }
}
