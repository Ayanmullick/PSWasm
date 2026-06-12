using PSWasm.Commands;
using PSWasm.Language;
using System.Net.Http;

namespace PSWasm;

public sealed class PowerShellWasmRuntime
{
    private readonly Dictionary<string, IPowerShellWasmCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly PowerShellWasmExecutionContext _executionContext;
    private readonly PowerShellWasmParser _parser = new();

    public PowerShellWasmRuntime(IDictionary<string, string>? environment = null, HttpMessageHandler? httpMessageHandler = null)
    {
        _executionContext = new PowerShellWasmExecutionContext(environment);
        var httpClient = httpMessageHandler is null ? new HttpClient() : new HttpClient(httpMessageHandler);

        RegisterCommand("Clear-Variable", new ClearVariableCommand());
        RegisterCommand("clv", new ClearVariableCommand());
        RegisterCommand("ConvertFrom-Csv", new ConvertFromCsvCommand());
        RegisterCommand("ConvertFrom-Json", new ConvertFromJsonCommand());
        RegisterCommand("ConvertTo-Json", new ConvertToJsonCommand());
        RegisterCommand("Format-List", new FormatListCommand());
        RegisterCommand("Format-Table", new FormatTableCommand());
        RegisterCommand("fl", new FormatListCommand());
        RegisterCommand("ft", new FormatTableCommand());
        RegisterCommand("Get-DomSession", new GetDomSessionCommand());
        RegisterCommand("Get-Command", new GetCommandCommand(() =>
            _commands.Keys.Concat(_executionContext.GetFunctionNames()).Distinct(StringComparer.OrdinalIgnoreCase)));
        RegisterCommand("gcm", new GetCommandCommand(() =>
            _commands.Keys.Concat(_executionContext.GetFunctionNames()).Distinct(StringComparer.OrdinalIgnoreCase)));
        RegisterCommand("Get-Culture", new GetCultureCommand());
        RegisterCommand("Get-Date", new GetDateCommand());
        RegisterCommand("Get-Time", new GetDateCommand(timeOnly: true));
        RegisterCommand("Get-TimeZone", new GetTimeZoneCommand());
        RegisterCommand("Get-UICulture", new GetCultureCommand(uiCulture: true));
        RegisterCommand("ForEach-Object", new ForEachObjectCommand());
        RegisterCommand("ForEach", new ForEachObjectCommand());
        RegisterCommand("Group-Object", new GroupObjectCommand());
        RegisterCommand("Group", new GroupObjectCommand());
        RegisterCommand("Get-Variable", new GetVariableCommand());
        RegisterCommand("gv", new GetVariableCommand());
        RegisterCommand("Invoke-WebRequest", new InvokeWebRequestCommand(httpClient));
        RegisterCommand("iwr", new InvokeWebRequestCommand(httpClient));
        RegisterCommand("Out-String", new OutStringCommand());
        RegisterCommand("Remove-Variable", new RemoveVariableCommand());
        RegisterCommand("rv", new RemoveVariableCommand());
        RegisterCommand("Select-Object", new SelectObjectCommand());
        RegisterCommand("Select", new SelectObjectCommand());
        RegisterCommand("Select-String", new SelectStringCommand());
        RegisterCommand("sls", new SelectStringCommand());
        RegisterCommand("Set-Variable", new SetVariableCommand());
        RegisterCommand("sv", new SetVariableCommand());
        RegisterCommand("Sort-Object", new SortObjectCommand());
        RegisterCommand("Sort", new SortObjectCommand());
        RegisterCommand("throw", new ThrowCommand());
        RegisterCommand("Measure-Object", new MeasureObjectCommand());
        RegisterCommand("Measure", new MeasureObjectCommand());
        RegisterCommand("New-DomSession", new NewDomSessionCommand());
        RegisterCommand("Remove-DomSession", new RemoveDomSessionCommand());
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
        _executionContext.ClearOutput();
        var ast = Parse(script);
        var executor = new PowerShellWasmAstExecutor(_executionContext, _commands);
        await executor.ExecuteAsync(ast, cancellationToken);
        return new PowerShellWasmResult([.. _executionContext.Records]);
    }
}
