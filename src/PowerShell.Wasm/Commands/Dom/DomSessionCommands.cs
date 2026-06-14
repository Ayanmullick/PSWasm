namespace PSWasm.Commands;

internal sealed class NewDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ExecutionContext.WriteOutput(context.ExecutionContext.NewDomSession(GetName(context), GetTarget(context)));
        return ValueTask.CompletedTask;
    }

    private static string? GetName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Name", out var name))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(name);
        }

        return context.Arguments.Count > 0 ? PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[0]) : null;
    }

    private static string? GetTarget(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("Target", out var target))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(target);
        }

        return context.Arguments.Count > 1 ? PowerShellWasmCommandUtilities.ToInvariantString(context.Arguments[1]) : null;
    }
}

internal sealed class GetDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var sessions = DomSessionCommandUtilities.SelectSessions(context, requireSelection: false);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.WriteOutput(session);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RemoveDomSessionCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var sessions = DomSessionCommandUtilities.SelectSessions(context, requireSelection: true).ToArray();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ExecutionContext.RemoveDomSession(Convert.ToInt32(session["Id"], System.Globalization.CultureInfo.InvariantCulture));
        }

        return ValueTask.CompletedTask;
    }
}
