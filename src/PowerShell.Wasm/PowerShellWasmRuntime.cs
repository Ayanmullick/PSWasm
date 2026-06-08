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
        RegisterCommand("Write-Output", new WriteOutputCommand());
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
