using PSWasm.Language;

namespace PSWasm.Commands;

internal sealed class ForEachObjectCommand : IPowerShellWasmPipelineCommand
{
    public IPowerShellWasmPipelineProcessor CreateProcessor(PowerShellWasmCommandContext context, bool hasPipelineInput) =>
        new Processor(context, hasPipelineInput);

    public async ValueTask InvokeAsync(PowerShellWasmCommandContext context, CancellationToken cancellationToken)
    {
        var processor = new Processor(context, context.PipelineInput.Count > 0);
        await processor.BeginAsync(cancellationToken);
        foreach (var record in context.PipelineInput) await processor.ProcessAsync(record, cancellationToken);
        await processor.CompleteAsync(cancellationToken);
    }

    // Behavioral guidance: one Begin/Process/End invocation, with each emitted record delivered before processing resumes.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/InternalCommands.cs
    private sealed class Processor : IPowerShellWasmPipelineProcessor
    {
        private readonly PowerShellWasmCommandContext _context;
        private readonly bool _hasPipelineInput;
        private readonly PowerShellWasmScriptBlock? _begin, _end;
        private readonly IReadOnlyList<PowerShellWasmScriptBlock> _process;
        private readonly string? _memberName;
        private readonly IReadOnlyList<object?> _memberArguments;

        public Processor(PowerShellWasmCommandContext context, bool hasPipelineInput)
        {
            _context = context;
            _hasPipelineInput = hasPipelineInput;
            (_begin, _process, _end) = GetScriptBlockBinding(context);
            _memberName = GetMemberName(context);
            _memberArguments = GetMemberArguments(context);
        }

        public async ValueTask BeginAsync(CancellationToken cancellationToken)
        {
            if (_begin is not null) await _begin.InvokeOutputAsync(null, NoPipelineVariables, cancellationToken);
        }

        public async ValueTask ProcessAsync(object? record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_begin is null && _process.Count == 0 && _end is null && string.IsNullOrWhiteSpace(_memberName))
            {
                await _context.ExecutionContext.WriteOutputAsync(record, cancellationToken);
                return;
            }

            var item = new PowerShellWasmPipelineInputItem(
                PowerShellWasmPipelineValue.Unwrap(record), PowerShellWasmPipelineValue.GetVariables(record));
            foreach (var process in _process)
                await process.InvokeOutputAsync(item.Value, item.Variables, cancellationToken);
            if (_process.Count == 0 && !string.IsNullOrWhiteSpace(_memberName))
                await WriteMemberOutputAsync(_context, item, _memberName, _memberArguments, cancellationToken);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            if (!_hasPipelineInput) await ProcessAsync(null, cancellationToken);
            if (_end is not null) await _end.InvokeOutputAsync(null, NoPipelineVariables, cancellationToken);
        }
    }

    private static (
        PowerShellWasmScriptBlock? Begin,
        IReadOnlyList<PowerShellWasmScriptBlock> Process,
        PowerShellWasmScriptBlock? End) GetScriptBlockBinding(PowerShellWasmCommandContext context)
    {
        var begin = GetParameterScriptBlock(context, "Begin");
        var process = GetParameterScriptBlock(context, "Process");
        var end = GetParameterScriptBlock(context, "End");
        var positional = context.Arguments.OfType<PowerShellWasmScriptBlock>().ToArray();

        if (begin is not null || process is not null || end is not null)
        {
            return (begin, process is not null ? [process] : positional.Take(1).ToArray(), end);
        }

        return positional.Length switch
        {
            0 => (null, [], null),
            1 => (null, positional, null),
            2 => (positional[0], [positional[1]], null),
            _ => (positional[0], positional.Skip(1).Take(positional.Length - 2).ToArray(), positional[^1])
        };
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

    private static async ValueTask WriteMemberOutputAsync(
        PowerShellWasmCommandContext context,
        PowerShellWasmPipelineInputItem item,
        string memberName,
        IReadOnlyList<object?> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 && PowerShellWasmCommandUtilities.TryGetMemberValue(item.Value, memberName, out var memberValue))
        {
            await WriteMemberValueAsync(context, memberValue, item.Variables, cancellationToken);
            return;
        }

        if (item.Value is not null &&
            PowerShellWasmDotNetBridge.TryGetInstanceMember(item.Value, memberName, out var method) &&
            PowerShellWasmDotNetBridge.TryInvoke(method, arguments, out var result))
        {
            await WriteMemberValueAsync(context, result, item.Variables, cancellationToken);
        }
    }

    // Behavioral guidance: ForEach-Object unrolls member results once; script-block results are already emitted records.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/InternalCommands.cs#L1150-L1165
    private static async ValueTask WriteMemberValueAsync(
        PowerShellWasmCommandContext context, object? value, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken)
    {
        foreach (var output in PowerShellWasmOutput.Enumerate(value))
        {
            await context.ExecutionContext.WriteOutputAsync(PowerShellWasmPipelineValue.Wrap(output, variables), cancellationToken);
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> NoPipelineVariables =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
