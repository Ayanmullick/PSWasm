using PSWasm.Language;

namespace PSWasm.Commands;

internal sealed class ForEachObjectCommand : IPowerShellWasmCommand
{
    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var begin = GetParameterScriptBlock(context, "Begin");
        var process = GetParameterScriptBlock(context, "Process") ?? context.Arguments.OfType<PowerShellWasmScriptBlock>().FirstOrDefault();
        var end = GetParameterScriptBlock(context, "End");
        var memberName = GetMemberName(context);
        if (begin is null && process is null && end is null && string.IsNullOrWhiteSpace(memberName))
        {
            foreach (var item in context.PipelineInput)
            {
                context.ExecutionContext.WriteOutput(item);
            }

            return;
        }

        if (begin is not null)
        {
            await WriteScriptBlockOutputAsync(context, begin, null, NoPipelineVariables, cancellationToken);
        }

        foreach (var item in PowerShellWasmCommandUtilities.EnumeratePipelineInput(context.PipelineInput))
        {
            if (process is not null)
            {
                await WriteScriptBlockOutputAsync(context, process, item.Value, item.Variables, cancellationToken);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(memberName))
            {
                WriteMemberOutput(context, item, memberName, GetMemberArguments(context));
            }
        }

        if (end is not null)
        {
            await WriteScriptBlockOutputAsync(context, end, null, NoPipelineVariables, cancellationToken);
        }
    }

    private static async ValueTask WriteScriptBlockOutputAsync(
        PowerShellWasmCommandContext context,
        PowerShellWasmScriptBlock scriptBlock,
        object? input,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        foreach (var output in await scriptBlock.InvokeAsync(input, null, variables, cancellationToken))
        {
            context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(output, variables));
        }
    }

    private static PowerShellWasmScriptBlock? GetParameterScriptBlock(PowerShellWasmCommandContext context, string parameterName) =>
        context.Parameters.TryGetValue(parameterName, out var value) && value is PowerShellWasmScriptBlock scriptBlock ? scriptBlock : null;

    private static string? GetMemberName(PowerShellWasmCommandContext context)
    {
        if (context.Parameters.TryGetValue("MemberName", out var memberName))
        {
            return PowerShellWasmCommandUtilities.ToInvariantString(memberName);
        }

        return context.Arguments
            .Where(static argument => argument is not PowerShellWasmScriptBlock)
            .Select(PowerShellWasmCommandUtilities.ToInvariantString)
            .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument));
    }

    private static IReadOnlyList<object?> GetMemberArguments(PowerShellWasmCommandContext context)
    {
        var arguments = new List<object?>();
        if (context.Parameters.TryGetValue("ArgumentList", out var argumentList))
        {
            arguments.AddRange(PowerShellWasmCommandUtilities.EnumerateInput([argumentList]));
        }

        if (!context.Parameters.ContainsKey("MemberName"))
        {
            arguments.AddRange(context.Arguments
                .Where(static argument => argument is not PowerShellWasmScriptBlock)
                .Skip(1));
        }

        return arguments;
    }

    private static void WriteMemberOutput(
        PowerShellWasmCommandContext context,
        PowerShellWasmPipelineInputItem item,
        string memberName,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0 && PowerShellWasmCommandUtilities.TryGetMemberValue(item.Value, memberName, out var memberValue))
        {
            context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(memberValue, item.Variables));
            return;
        }

        if (item.Value is not null &&
            PowerShellWasmDotNetBridge.TryGetInstanceMember(item.Value, memberName, out var method) &&
            PowerShellWasmDotNetBridge.TryInvoke(method, arguments, out var result))
        {
            context.ExecutionContext.WriteOutput(PowerShellWasmPipelineValue.Wrap(result, item.Variables));
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> NoPipelineVariables =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
