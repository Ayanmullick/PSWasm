using PSWasm.Language;
using System.Globalization;

namespace PSWasm;

public sealed class PowerShellWasmExecutionContext
{
    private const string ErrorRecordDataKey = "PSWasm.ErrorRecord";

    private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PowerShellWasmScriptFunction> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environment;
    private readonly Dictionary<int, Dictionary<string, object?>> _domSessions = [];
    private readonly List<object?> _errors = [];
    private readonly List<object?> _output = [];
    private readonly Stack<List<object?>> _outputCaptures = [];
    private int _nextDomSessionId = 1;
    private int _failureSignalCount;

    public PowerShellWasmExecutionContext(IDictionary<string, string>? environment = null)
    {
        _environment = environment is null ? new(StringComparer.OrdinalIgnoreCase) : new(environment, StringComparer.OrdinalIgnoreCase);
        InitializeAutomaticVariables();
    }

    public IReadOnlyList<PowerShellWasmOutputRecord> Records => _output.Select(FormatRecord).ToArray();
    public IReadOnlyList<string> Output => Records.Select(FormatRecordLine).ToArray();
    public int ErrorCount => _errors.Count;
    internal int FailureSignalCount => _failureSignalCount;

    internal void ClearOutput() =>
        _output.Clear();

    public string? GetEnvironmentVariable(string name) =>
        _environment.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

    public object? GetVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : null;

    public IReadOnlyDictionary<string, object?> GetVariables() =>
        new Dictionary<string, object?>(_variables, StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, object?> NewDomSession(string? name, string? target)
    {
        var id = _nextDomSessionId++;
        var session = CreateDomSessionRecord(
            id,
            string.IsNullOrWhiteSpace(name) ? $"DomSession{id}" : name,
            string.IsNullOrWhiteSpace(target) ? "document" : target);

        _domSessions[id] = session;
        return CloneDomSession(session);
    }

    internal IReadOnlyList<Dictionary<string, object?>> GetDomSessions() =>
        _domSessions.Values.OrderBy(static session => session["Id"]).Select(CloneDomSession).ToArray();

    internal bool RemoveDomSession(int id) =>
        _domSessions.Remove(id);

    public void SetVariable(string name, object? value) =>
        _variables[name] = value;

    public void ClearVariable(string name) =>
        _variables[name] = null;

    public void RemoveVariable(string name) =>
        _variables.Remove(name);

    internal void SetFunction(PowerShellWasmScriptFunction function) =>
        _functions[function.Name] = function;

    internal bool TryGetFunction(string name, out PowerShellWasmScriptFunction function) =>
        _functions.TryGetValue(name, out function!);

    internal IEnumerable<string> GetFunctionNames() =>
        _functions.Keys;

    internal IDisposable WithVariableScope(IReadOnlyDictionary<string, object?> variables)
    {
        var snapshot = new Dictionary<string, object?>(_variables, StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            _variables[variable.Key] = variable.Value;
        }

        return new VariableScope(this, snapshot);
    }

    internal IDisposable WithTemporaryVariables(IReadOnlyDictionary<string, object?> variables)
    {
        var snapshot = variables.ToDictionary(
            static variable => variable.Key,
            variable => (_variables.TryGetValue(variable.Key, out var value), value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            _variables[variable.Key] = variable.Value;
        }

        return new TemporaryVariableScope(this, snapshot);
    }

    internal IDisposable WithPipelineItem(object? value)
    {
        var hadUnderscore = _variables.TryGetValue("_", out var underscore);
        var hadPSItem = _variables.TryGetValue("PSItem", out var psItem);
        _variables["_"] = value;
        _variables["PSItem"] = value;
        return new PipelineItemScope(this, hadUnderscore, underscore, hadPSItem, psItem);
    }

    public void WriteOutput(object? value)
    {
        if (value is null)
        {
            return;
        }

        ActiveOutput.Add(value);
    }

    public void WriteStream(string streamName, object? value)
    {
        if (value is null)
        {
            return;
        }

        var preferenceName = GetStreamPreferenceName(streamName);
        var preference = preferenceName is null ? "Continue" : GetPreferenceValue(preferenceName, "Continue");
        if (preference.Equals("Ignore", StringComparison.OrdinalIgnoreCase))
        {
            if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                SetLastCommandSucceeded(false);
            }

            return;
        }

        if (preference.Equals("SilentlyContinue", StringComparison.OrdinalIgnoreCase))
        {
            if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                RecordError(value);
            }

            return;
        }

        if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            RecordError(value);
        }

        ActiveOutput.Add(new PowerShellWasmStreamRecord(streamName, value));

        if (IsStoppingPreference(preference))
        {
            var message = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            throw new InvalidOperationException(
                $"The running command stopped because the preference variable \"{preferenceName}\" is set to {preference}: {message}");
        }
    }

    internal void SetLastCommandSucceeded(bool succeeded)
    {
        if (!succeeded)
        {
            _failureSignalCount++;
        }

        _variables["?"] = succeeded;
    }

    internal string OutputFieldSeparator =>
        GetPreferenceValue("OFS", " ");

    internal Dictionary<string, object?> RecordException(Exception error)
    {
        if (error.Data[ErrorRecordDataKey] is Dictionary<string, object?> existingRecord)
        {
            return existingRecord;
        }

        var record = CreateErrorRecord(error.Message, error.GetType().Name, error.GetType().FullName ?? error.GetType().Name);
        error.Data[ErrorRecordDataKey] = record;
        PushErrorRecord(record, error.ToString());
        return record;
    }

    internal IDisposable CaptureOutput(List<object?> output)
    {
        _outputCaptures.Push(output);
        return new OutputCapture(this, output);
    }

    internal void WriteCapturedOutput(IEnumerable<object?> output)
    {
        foreach (var item in output)
        {
            if (item is not null)
            {
                ActiveOutput.Add(item);
            }
        }
    }

    internal IReadOnlyList<object?> GetCapturedOutput(IEnumerable<object?> output) =>
        output.Where(static item => item is not null and not PowerShellWasmStreamRecord).ToArray();

    internal IReadOnlyList<object?> GetCapturedStreamValues(IEnumerable<object?> output, string streamName) =>
        output.OfType<PowerShellWasmStreamRecord>()
            .Where(record => record.StreamName.Equals(streamName, StringComparison.OrdinalIgnoreCase))
            .Select(static record => record.Value)
            .Where(static value => value is not null)
            .ToArray();

    internal IReadOnlyList<object?> GetErrorsAddedAfter(int errorCount)
    {
        var added = _errors.Count - errorCount;
        return added <= 0 ? [] : _errors.Take(added).Reverse<object?>().ToArray();
    }

    internal void SetCommonParameterVariable(string variableName, IReadOnlyList<object?> values)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return;
        }

        var append = variableName.StartsWith("+", StringComparison.Ordinal);
        if (append)
        {
            variableName = variableName[1..];
        }

        var existing = append ? EnumerateVariableValue(GetVariable(variableName)) : [];
        _variables[variableName] = existing.Concat(values).ToArray();
    }

    private List<object?> ActiveOutput =>
        _outputCaptures.TryPeek(out var output) ? output : _output;

    private void InitializeAutomaticVariables()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var version = typeof(PowerShellWasmRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _variables["?"] = true;
        _variables["args"] = Array.Empty<object?>();
        _variables["ConfirmPreference"] = "High";
        _variables["DebugPreference"] = "SilentlyContinue";
        _variables["EnabledExperimentalFeatures"] = Array.Empty<object?>();
        _variables["Error"] = Array.Empty<object?>();
        _variables["ErrorActionPreference"] = "Continue";
        _variables["ErrorView"] = "ConciseView";
        _variables["FormatEnumerationLimit"] = 4;
        _variables["HOME"] = "browser:/home";
        _variables["Host"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "PSWasm Browser Host",
            ["Version"] = version,
            ["CurrentCulture"] = culture.Name,
            ["CurrentUICulture"] = uiCulture.Name
        };
        _variables["InformationPreference"] = "SilentlyContinue";
        _variables["input"] = Array.Empty<object?>();
        _variables["IsCoreCLR"] = true;
        _variables["IsLinux"] = false;
        _variables["IsMacOS"] = false;
        _variables["IsWindows"] = false;
        _variables["Matches"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _variables["NestedPromptLevel"] = 0;
        _variables["OFS"] = " ";
        _variables["OutputEncoding"] = "utf-8";
        _variables["ProgressPreference"] = "Continue";
        _variables["PSBoundParameters"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _variables["PSCommandPath"] = string.Empty;
        _variables["PSCulture"] = culture.Name;
        _variables["PSDefaultParameterValues"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _variables["PSDebugContext"] = null;
        _variables["PSEdition"] = "Core";
        _variables["PSHOME"] = "browser:/pswasm";
        _variables["PSScriptRoot"] = string.Empty;
        _variables["PSUICulture"] = uiCulture.Name;
        _variables["PSVersionTable"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PSVersion"] = "7.5.0",
            ["PSEdition"] = "Core",
            ["GitCommitId"] = "PSWasm",
            ["OS"] = "Browser",
            ["Platform"] = "Browser",
            ["PSCompatibleVersions"] = new object?[] { "7.5" },
            ["PSWasmVersion"] = version
        };
        _variables["PWD"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = "browser:/",
            ["Provider"] = "Browser"
        };
        _variables["ShellId"] = "PSWasm";
        _variables["StackTrace"] = null;
        _variables["VerbosePreference"] = "SilentlyContinue";
        _variables["WarningPreference"] = "Continue";
        _variables["WhatIfPreference"] = false;
    }

    private string GetPreferenceValue(string name, string fallback) =>
        Convert.ToString(GetVariable(name), CultureInfo.InvariantCulture) ?? fallback;

    private static bool IsStoppingPreference(string preference) =>
        preference.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
        preference.Equals("Break", StringComparison.OrdinalIgnoreCase) ||
        preference.Equals("Inquire", StringComparison.OrdinalIgnoreCase) ||
        preference.Equals("Suspend", StringComparison.OrdinalIgnoreCase);

    private static string? GetStreamPreferenceName(string streamName) =>
        streamName.ToLowerInvariant() switch
        {
            "debug" => "DebugPreference",
            "error" => "ErrorActionPreference",
            "information" => "InformationPreference",
            "progress" => "ProgressPreference",
            "verbose" => "VerbosePreference",
            "warning" => "WarningPreference",
            _ => null
        };

    private void RecordError(object? value)
    {
        var message = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        PushErrorRecord(CreateErrorRecord(message, "Error", "PSWasm.WriteError"), null);
    }

    private void PushErrorRecord(Dictionary<string, object?> record, string? stackTrace)
    {
        _errors.Insert(0, record);
        _variables["Error"] = _errors.ToArray();
        _variables["StackTrace"] = stackTrace;
        SetLastCommandSucceeded(false);
    }

    private static Dictionary<string, object?> CreateErrorRecord(string message, string exception, string fullyQualifiedErrorId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Message"] = message,
            ["Exception"] = exception,
            ["FullyQualifiedErrorId"] = fullyQualifiedErrorId
        };

    private static Dictionary<string, object?> CreateDomSessionRecord(int id, string name, string target) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Name"] = name,
            ["Target"] = target,
            ["State"] = "Opened",
            ["SessionType"] = "Dom"
        };

    private static Dictionary<string, object?> CloneDomSession(Dictionary<string, object?> session) =>
        new(session, StringComparer.OrdinalIgnoreCase);

    private void ReleaseOutputCapture(List<object?> output)
    {
        if (!_outputCaptures.TryPop(out var current) || !ReferenceEquals(current, output))
        {
            throw new InvalidOperationException("Output capture stack is unbalanced.");
        }
    }

    private void RestorePipelineItem(bool hadUnderscore, object? underscore, bool hadPSItem, object? psItem)
    {
        RestoreVariable("_", hadUnderscore, underscore);
        RestoreVariable("PSItem", hadPSItem, psItem);
    }

    private void RestoreTemporaryVariables(
        IReadOnlyDictionary<string, (bool HadValue, object? Value)> variables)
    {
        foreach (var variable in variables)
        {
            RestoreVariable(variable.Key, variable.Value.HadValue, variable.Value.Value);
        }
    }

    private void RestoreVariable(string name, bool hadValue, object? value)
    {
        if (hadValue)
        {
            _variables[name] = value;
        }
        else
        {
            _variables.Remove(name);
        }
    }

    private static IEnumerable<object?> EnumerateVariableValue(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private void RestoreVariables(Dictionary<string, object?> variables)
    {
        _variables.Clear();
        foreach (var variable in variables)
        {
            _variables[variable.Key] = variable.Value;
        }
    }

    private static PowerShellWasmOutputRecord FormatRecord(object? value) =>
        value switch
        {
            PowerShellWasmStreamRecord stream => new(stream.StreamName, FormatOutput(stream.Value)),
            _ => new("Output", FormatOutput(value))
        };

    private static string FormatRecordLine(PowerShellWasmOutputRecord record) =>
        record.Stream.Equals("Output", StringComparison.OrdinalIgnoreCase)
            ? record.Text
            : $"[{record.Stream}] {record.Text}";

    private static string FormatOutput(object? value) =>
        value switch
        {
            null => string.Empty,
            PowerShellWasmStreamRecord stream => FormatOutput(stream.Value),
            Dictionary<string, object?> hashtable => "@{" + string.Join("; ", hashtable.Select(static item => $"{item.Key}={FormatOutput(item.Value)}")) + "}",
            object?[] array => string.Join(Environment.NewLine, array.Select(FormatOutput)),
            _ => value.ToString() ?? string.Empty
        };

    private sealed record PowerShellWasmStreamRecord(string StreamName, object? Value);

    private sealed class OutputCapture(PowerShellWasmExecutionContext context, List<object?> output) : IDisposable
    {
        public void Dispose() =>
            context.ReleaseOutputCapture(output);
    }

    private sealed class VariableScope(PowerShellWasmExecutionContext context, Dictionary<string, object?> variables) : IDisposable
    {
        public void Dispose() =>
            context.RestoreVariables(variables);
    }

    private sealed class TemporaryVariableScope(
        PowerShellWasmExecutionContext context,
        IReadOnlyDictionary<string, (bool HadValue, object? Value)> variables) : IDisposable
    {
        public void Dispose() =>
            context.RestoreTemporaryVariables(variables);
    }

    private sealed class PipelineItemScope(
        PowerShellWasmExecutionContext context,
        bool hadUnderscore,
        object? underscore,
        bool hadPSItem,
        object? psItem) : IDisposable
    {
        public void Dispose() =>
            context.RestorePipelineItem(hadUnderscore, underscore, hadPSItem, psItem);
    }
}

internal sealed record PowerShellWasmScriptFunction(
    string Name,
    IReadOnlyList<string> ParameterNames,
    ScriptAst Body);
