namespace PSWasm.Commands;

internal sealed class GetDomTextCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var selectors = DomCommandUtilities.GetSelectors(context);
        if (selectors.Count == 0)
        {
            throw new InvalidOperationException("Get-DomText requires at least one selector.");
        }

        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(await host.GetTextAsync(selector, cancellationToken));
        }
    }
}

internal sealed class SetDomTextCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var selector = DomCommandUtilities.GetRequiredText(context, "Selector", 0);
        var text = PowerShellWasmCommandUtilities.FormatValue(DomCommandUtilities.GetRequiredValue(context, "Text", 1));
        await host.SetTextAsync(selector, text, cancellationToken);
    }
}

internal sealed class GetDomValueCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var selectors = DomCommandUtilities.GetSelectors(context);
        if (selectors.Count == 0)
        {
            throw new InvalidOperationException("Get-DomValue requires at least one selector.");
        }

        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(await host.GetValueAsync(selector, cancellationToken));
        }
    }
}

internal sealed class SetDomPropertyCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var selector = DomCommandUtilities.GetRequiredText(context, "Selector", 0);
        var propertyName = GetPropertyName(context);
        var value = DomCommandUtilities.GetRequiredValue(context, "Value", 2);
        await host.SetPropertyAsync(selector, propertyName, value, cancellationToken);
    }

    private static string GetPropertyName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Property", out var property))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(property);
        }

        if (context.Parameters.TryGetValue("Name", out var name))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(name);
        }

        if (context.Arguments.Count > 1)
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[1]);
        }

        throw new InvalidOperationException("Property is required.");
    }
}

internal sealed class RegisterDomEventCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var host = DomCommandUtilities.GetDomHost(context);
        var selector = DomCommandUtilities.GetRequiredText(context, "Selector", 0);
        var eventName = DomCommandUtilities.GetRequiredText(context, "Event", 1);
        var scriptBlock = PowerShellWasmCommandUtilities.GetScriptBlock(context, "ScriptBlock", "Action");
        if (scriptBlock is null)
        {
            throw new InvalidOperationException("Register-DomEvent requires a script block.");
        }

        var session = DomSessionCommandUtilities.GetSession(context);
        var registration = new PowerShellWasmDomEventRegistration(
            context.ExecutionContext.GetNextDomEventRegistrationId(),
            session,
            selector,
            eventName,
            PowerShellWasmCommandUtilities.ToBoolean(context.Parameters.GetValueOrDefault("PreventDefault")),
            scriptBlock);

        await host.RegisterEventAsync(registration, cancellationToken);
        context.ExecutionContext.WriteOutput(CreateRecord(registration));
    }

    private static Dictionary<string, object?> CreateRecord(PowerShellWasmDomEventRegistration registration) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = registration.Id,
            ["SessionId"] = registration.Session is null ? null : registration.Session["Id"],
            ["SessionName"] = registration.Session is null ? null : registration.Session["Name"],
            ["Selector"] = registration.Selector,
            ["Event"] = registration.Event,
            ["PreventDefault"] = registration.PreventDefault,
            ["RegistrationType"] = "DomEvent"
        };
}
