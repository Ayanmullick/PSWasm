namespace PSWasm;

public interface IPowerShellWasmDomHost
{
    ValueTask<string> GetTextAsync(string selector, CancellationToken cancellationToken);

    ValueTask SetTextAsync(string selector, string text, CancellationToken cancellationToken);

    ValueTask SetHtmlAsync(string selector, string html, CancellationToken cancellationToken);

    ValueTask<string> GetValueAsync(string selector, CancellationToken cancellationToken);

    ValueTask SetPropertyAsync(string selector, string propertyName, object? value, CancellationToken cancellationToken);

    ValueTask<object?> GetPropertyAsync(string selector, string propertyName, CancellationToken cancellationToken);

    ValueTask RegisterEventAsync(PowerShellWasmDomEventRegistration registration, CancellationToken cancellationToken);

    ValueTask UnregisterEventAsync(int registrationId, CancellationToken cancellationToken);

    ValueTask<string?> GetStorageItemAsync(string storage, string key, CancellationToken cancellationToken);

    ValueTask SetStorageItemAsync(string storage, string key, string value, CancellationToken cancellationToken);

    ValueTask RemoveStorageItemAsync(string storage, string key, CancellationToken cancellationToken);

    ValueTask ClearStorageAsync(string storage, CancellationToken cancellationToken);

    ValueTask RegisterStorageBindingAsync(PowerShellWasmDomStorageBindingRegistration registration, CancellationToken cancellationToken);

    ValueTask UnregisterStorageBindingAsync(int registrationId, CancellationToken cancellationToken);
}

public sealed record PowerShellWasmDomEventRegistration(
    int Id,
    Dictionary<string, object?>? Session,
    string Selector,
    string Event,
    bool PreventDefault,
    PowerShellWasmScriptBlock ScriptBlock);

public sealed record PowerShellWasmDomStorageBindingRegistration(
    int Id,
    Dictionary<string, object?>? Session,
    string Storage,
    IReadOnlyDictionary<string, string> Map,
    string Event,
    string Property);
