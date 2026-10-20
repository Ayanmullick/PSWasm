namespace PSWasm.Commands;

internal sealed class GetDomStorageItemCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var storage = DomCommandUtilities.GetStorage(context);
        var keys = DomCommandUtilities.GetKeys(context);
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("Get-DomStorageItem requires at least one key.");
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await host.GetStorageItemAsync(storage, key, cancellationToken);
            if (value is not null)
            {
                await context.ExecutionContext.WriteOutputAsync(value, cancellationToken);
            }
        }
    }
}

internal sealed class SetDomStorageItemCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var storage = DomCommandUtilities.GetStorage(context);
        var key = DomCommandUtilities.GetRequiredText(context, "Key", 0);
        var value = PowerShellWasmCommandUtilities.FormatValue(DomCommandUtilities.GetRequiredValue(context, "Value", 1));
        await host.SetStorageItemAsync(storage, key, value, cancellationToken);
    }
}

internal sealed class RemoveDomStorageItemCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var storage = DomCommandUtilities.GetStorage(context);
        var keys = DomCommandUtilities.GetKeys(context);
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("Remove-DomStorageItem requires at least one key.");
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.RemoveStorageItemAsync(storage, key, cancellationToken);
        }
    }
}

internal sealed class ClearDomStorageCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        await host.ClearStorageAsync(DomCommandUtilities.GetStorage(context), cancellationToken);
    }
}

internal sealed class RegisterDomStorageBindingCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var session = DomSessionCommandUtilities.GetSession(context);
        var registration = new PowerShellWasmDomStorageBindingRegistration(
            context.ExecutionContext.GetNextDomEventRegistrationId(),
            session,
            DomCommandUtilities.GetStorage(context),
            GetMap(context),
            GetOptionalText(context, "Event", "Input"),
            GetOptionalText(context, "Property", "Value"));

        await host.RegisterStorageBindingAsync(registration, cancellationToken);
        await context.ExecutionContext.WriteOutputAsync(CreateRecord(registration), cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> GetMap(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Map", out var mapValue) ||
            TryGetPositionalMap(context, out mapValue))
        {
            return ConvertMap(mapValue);
        }

        var selectors = DomCommandUtilities.GetSelectors(context);
        var keys = DomCommandUtilities.GetKeys(context);
        if (selectors.Count == 0 || keys.Count == 0)
        {
            throw new InvalidOperationException("Register-DomStorageBinding requires a map or selector and key.");
        }

        if (selectors.Count != keys.Count)
        {
            throw new InvalidOperationException("Register-DomStorageBinding selector and key counts must match.");
        }

        return selectors.Zip(keys).ToDictionary(
            static pair => pair.First,
            static pair => pair.Second,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetPositionalMap(PowerShellWasmCommandContext context, out object? mapValue)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is System.Collections.IDictionary or IReadOnlyDictionary<string, object?>)
            {
                mapValue = argument;
                return true;
            }
        }

        mapValue = null;
        return false;
    }

    private static IReadOnlyDictionary<string, string> ConvertMap(object? mapValue)
    {
        if (mapValue is IReadOnlyDictionary<string, object?> readOnlyDictionary && readOnlyDictionary.Count > 0)
        {
            return readOnlyDictionary.ToDictionary(
                static item => item.Key,
                static item => PowerShellWasmCommandUtilities.ToInvariantString(item.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (mapValue is System.Collections.IDictionary dictionary && dictionary.Count > 0)
        {
            // Selectors and storage keys are strings at this browser API boundary; retain raw keys in runtime dictionaries.
            return dictionary.Cast<System.Collections.DictionaryEntry>().ToDictionary(
                static item => PowerShellWasmCommandUtilities.ToInvariantString(item.Key),
                static item => PowerShellWasmCommandUtilities.ToInvariantString(item.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        throw new InvalidOperationException("Register-DomStorageBinding -Map must be a non-empty hashtable.");
    }

    private static string GetOptionalText(PowerShellWasmCommandContext context, string parameterName, string fallback) =>
        context.Parameters.TryGetValue(parameterName, out var value)
            ? PowerShellWasmCommandUtilities.ToInvariantString(value)
            : fallback;

    private static Dictionary<string, object?> CreateRecord(PowerShellWasmDomStorageBindingRegistration registration) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = registration.Id,
            ["SessionId"] = registration.Session is null ? null : registration.Session["Id"],
            ["SessionName"] = registration.Session is null ? null : registration.Session["Name"],
            ["Storage"] = registration.Storage,
            ["Map"] = new Dictionary<string, string>(registration.Map, StringComparer.OrdinalIgnoreCase),
            ["Event"] = registration.Event,
            ["Property"] = registration.Property,
            ["RegistrationType"] = "DomStorageBinding"
        };
}

internal sealed class UnregisterDomStorageBindingCommand : IPowerShellWasmPipelineCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var ids = DomCommandUtilities.GetIds(context);
        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                "Unregister-DomStorageBinding requires at least one registration id or registration object.");
        }

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.UnregisterStorageBindingAsync(id, cancellationToken);
        }
    }
}
