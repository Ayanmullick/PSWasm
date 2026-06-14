namespace PSWasm;

public interface IPowerShellWasmDomHost
{
    ValueTask<string> GetTextAsync(string selector, CancellationToken cancellationToken);

    ValueTask SetTextAsync(string selector, string text, CancellationToken cancellationToken);

    ValueTask<string> GetValueAsync(string selector, CancellationToken cancellationToken);

    ValueTask SetPropertyAsync(string selector, string propertyName, object? value, CancellationToken cancellationToken);

    ValueTask RegisterEventAsync(PowerShellWasmDomEventRegistration registration, CancellationToken cancellationToken);
}

public sealed record PowerShellWasmDomEventRegistration(
    int Id,
    Dictionary<string, object?>? Session,
    string Selector,
    string Event,
    bool PreventDefault,
    PowerShellWasmScriptBlock ScriptBlock);
