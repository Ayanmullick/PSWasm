using PSWasm.Commands;
using PSWasm.Language;
#if PSWASM_WEB
using System.Net.Http;
#endif

namespace PSWasm;

public sealed class PowerShellWasmRuntime
{
    private readonly Dictionary<string, IPowerShellWasmCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly PowerShellWasmExecutionContext _executionContext;
    private readonly PowerShellWasmParser _parser = new();

    public PowerShellWasmRuntime(
        IDictionary<string, string>? environment = null,
#if PSWASM_WEB
        HttpMessageHandler? httpMessageHandler = null,
#endif
        IPowerShellWasmDomHost? domHost = null,
        IPowerShellWasmAzureAuthHost? azureAuthHost = null)
    {
        _executionContext = new PowerShellWasmExecutionContext(environment, domHost);
#if PSWASM_WEB
        var httpClient = httpMessageHandler is null ? new HttpClient() : new HttpClient(httpMessageHandler);
#endif

        RegisterCommand("Clear-Variable", new ClearVariableCommand());
        RegisterCommand("clv", new ClearVariableCommand());
        RegisterCommand("&", new CallOperatorCommand());
#if PSWASM_AZURE_AUTH
        RegisterCommand("Connect-AzAccount", new ConnectAzAccountCommand(azureAuthHost));
#endif
        RegisterCommand("ConvertFrom-Csv", new ConvertFromCsvCommand());
        RegisterCommand("ConvertFrom-Json", new ConvertFromJsonCommand());
        RegisterCommand("ConvertTo-Html", new ConvertToHtmlCommand());
        RegisterCommand("ConvertTo-Json", new ConvertToJsonCommand());
#if PSWASM_AZURE_AUTH
        RegisterCommand("Disconnect-AzAccount", new DisconnectAzAccountCommand(azureAuthHost));
#endif
        RegisterCommand("Format-List", new FormatListCommand());
        RegisterCommand("Format-Table", new FormatTableCommand());
        RegisterCommand("fl", new FormatListCommand());
        RegisterCommand("ft", new FormatTableCommand());
#if PSWASM_DOM
        RegisterCommand("Get-DomSession", new GetDomSessionCommand());
        RegisterCommand("Get-DomProperty", new GetDomPropertyCommand());
        RegisterCommand("Get-DomStorageItem", new GetDomStorageItemCommand());
        RegisterCommand("Get-DomText", new GetDomTextCommand());
        RegisterCommand("Get-DomValue", new GetDomValueCommand());
#endif
        RegisterCommand("Get-Command", new GetCommandCommand(() =>
            _commands.Keys.Concat(_executionContext.GetFunctionNames()).Distinct(StringComparer.OrdinalIgnoreCase)));
        RegisterCommand("gcm", new GetCommandCommand(() =>
            _commands.Keys.Concat(_executionContext.GetFunctionNames()).Distinct(StringComparer.OrdinalIgnoreCase)));
        RegisterCommand("Get-Culture", new GetCultureCommand());
        RegisterCommand("Get-Date", new GetDateCommand());
#if PSWASM_AZURE_AUTH
        RegisterCommand("Get-AzAccessToken", new GetAzAccessTokenCommand(azureAuthHost));
        RegisterCommand("Get-AzContext", new GetAzContextCommand(azureAuthHost));
#endif
        RegisterCommand("Get-Time", new GetDateCommand(timeOnly: true));
        RegisterCommand("Get-TimeZone", new GetTimeZoneCommand());
        RegisterCommand("Get-UICulture", new GetCultureCommand(uiCulture: true));
        RegisterCommand("ForEach-Object", new ForEachObjectCommand());
        RegisterCommand("ForEach", new ForEachObjectCommand());
        RegisterCommand("%", new ForEachObjectCommand());
        RegisterCommand("Group-Object", new GroupObjectCommand());
        RegisterCommand("Group", new GroupObjectCommand());
        RegisterCommand("Get-Variable", new GetVariableCommand());
        RegisterCommand("gv", new GetVariableCommand());
#if PSWASM_WEB
        RegisterCommand("Invoke-WebRequest", new InvokeWebRequestCommand(httpClient));
        RegisterCommand("iwr", new InvokeWebRequestCommand(httpClient));
        RegisterCommand("Invoke-RestMethod", new InvokeRestMethodCommand(httpClient));
        RegisterCommand("irm", new InvokeRestMethodCommand(httpClient));
#endif
        RegisterCommand("Out-String", new OutStringCommand());
        RegisterCommand("Remove-Variable", new RemoveVariableCommand());
        RegisterCommand("rv", new RemoveVariableCommand());
        RegisterCommand("Select-Object", new SelectObjectCommand());
        RegisterCommand("Select", new SelectObjectCommand());
        RegisterCommand("Select-String", new SelectStringCommand());
        RegisterCommand("sls", new SelectStringCommand());
#if PSWASM_DOM
        RegisterCommand("Set-DomHtml", new SetDomHtmlCommand());
        RegisterCommand("Clear-DomStorage", new ClearDomStorageCommand());
        RegisterCommand("Set-DomProperty", new SetDomPropertyCommand());
        RegisterCommand("Set-DomStorageItem", new SetDomStorageItemCommand());
        RegisterCommand("Set-DomText", new SetDomTextCommand());
        RegisterCommand("Set-DomValue", new SetDomValueCommand());
#endif
        RegisterCommand("Set-Variable", new SetVariableCommand());
        RegisterCommand("sv", new SetVariableCommand());
        RegisterCommand("Sort-Object", new SortObjectCommand());
        RegisterCommand("Sort", new SortObjectCommand());
        RegisterCommand("throw", new ThrowCommand());
        RegisterCommand("Measure-Object", new MeasureObjectCommand());
        RegisterCommand("Measure", new MeasureObjectCommand());
        RegisterCommand("New-Object", new NewObjectCommand());
#if PSWASM_DOM
        RegisterCommand("New-DomSession", new NewDomSessionCommand());
        RegisterCommand("Register-DomEvent", new RegisterDomEventCommand());
        RegisterCommand("Register-DomStorageBinding", new RegisterDomStorageBindingCommand());
        RegisterCommand("Remove-DomStorageItem", new RemoveDomStorageItemCommand());
        RegisterCommand("Remove-DomSession", new RemoveDomSessionCommand());
        RegisterCommand("Unregister-DomEvent", new UnregisterDomEventCommand());
        RegisterCommand("Unregister-DomStorageBinding", new UnregisterDomStorageBindingCommand());
#endif
        RegisterCommand("Where-Object", new WhereObjectCommand());
        RegisterCommand("Where", new WhereObjectCommand());
        RegisterCommand("?", new WhereObjectCommand());
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
