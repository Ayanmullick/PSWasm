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
        RegisterCommand("ConvertFrom-Json", new ConvertFromJsonCommand());
        RegisterCommand("ConvertTo-Json", new ConvertToJsonCommand());
        RegisterCommand("Get-Date", new GetDateCommand());
        RegisterCommand("Get-Time", new GetDateCommand(timeOnly: true));
        RegisterCommand("Get-TimeZone", new GetTimeZoneCommand());
        RegisterCommand("ForEach-Object", new ForEachObjectCommand());
        RegisterCommand("ForEach", new ForEachObjectCommand());
        RegisterCommand("Group-Object", new GroupObjectCommand());
        RegisterCommand("Group", new GroupObjectCommand());
        RegisterCommand("Out-String", new OutStringCommand());
        RegisterCommand("Select-Object", new SelectObjectCommand());
        RegisterCommand("Select", new SelectObjectCommand());
        RegisterCommand("Sort-Object", new SortObjectCommand());
        RegisterCommand("Sort", new SortObjectCommand());
        RegisterCommand("Measure-Object", new MeasureObjectCommand());
        RegisterCommand("Measure", new MeasureObjectCommand());
        RegisterCommand("Where-Object", new WhereObjectCommand());
        RegisterCommand("Where", new WhereObjectCommand());
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
        return new PowerShellWasmResult([.. _executionContext.Records]);
    }
}
