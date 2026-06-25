using PSWasm.Language;
using System.Globalization;

namespace PSWasm.Commands;

// PowerShell source reference:
// - https://github.com/PowerShell/PowerShell/blob/master/src/Microsoft.PowerShell.Commands.Utility/commands/utility/New-Object.cs
// Browser note: this keeps the PowerShell command shape, but object construction is limited by
// PowerShellWasmDotNetBridge instead of opening arbitrary .NET reflection.
internal sealed class NewObjectCommand : IPowerShellWasmCommand
{
    public ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var typeName = GetTypeName(context);
        var arguments = GetConstructorArguments(context).ToArray();
        if (!PowerShellWasmDotNetBridge.TryConstruct(typeName, arguments, out var value))
        {
            throw new InvalidOperationException($"Type [{typeName}] is not available in this browser-safe runtime.");
        }

        context.ExecutionContext.WriteOutput(value);
        return ValueTask.CompletedTask;
    }

    private static string GetTypeName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("TypeName", out var parameterTypeName))
        {
            var typeName = Convert.ToString(parameterTypeName, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return typeName;
            }
        }

        if (context.Arguments.Count > 0)
        {
            var typeName = Convert.ToString(context.Arguments[0], CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return typeName;
            }
        }

        throw new InvalidOperationException("New-Object requires a TypeName.");
    }

    private static IEnumerable<object?> GetConstructorArguments(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("ArgumentList", out var argumentList) ||
            context.Parameters.TryGetValue("Args", out argumentList))
        {
            foreach (var argument in PowerShellWasmCommandUtilities.EnumerateInput([argumentList]))
            {
                yield return argument;
            }

            yield break;
        }

        var skip = context.Parameters.ContainsKey("TypeName") ? 0 : 1;
        foreach (var argument in context.Arguments.Skip(skip))
        {
            yield return argument;
        }
    }
}
