using PSWasm.Language;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PSWasm;

public sealed class PowerShellWasmExecutionContext
{
    private const string ErrorRecordDataKey = "PSWasm.ErrorRecord";

    private VariableFrame _frame = new(null, isOverlay: false);
    private Dictionary<string, object?> _variables => _frame.Values;
    private readonly Dictionary<string, string> _environment;
    private readonly Dictionary<int, Dictionary<string, object?>> _domSessions = [];
    private readonly List<object?> _errors = [];
    private readonly List<object?> _output = [];
    private OutputPort? _outputPort;
    private ErrorObserver? _errorObserver;
    private VariableFrame? _scriptCallerFrame;
    private int _nextDomSessionId = 1;
    private int _nextDomEventRegistrationId = 1;
    private bool _lastCommandSucceeded = true;
    private PipelineStatus? _pipelineStatus;
    private Exception? _currentException;
    private readonly ConditionalWeakTable<object, ErrorIdentity> _errorExceptions = new();

    public PowerShellWasmExecutionContext(IDictionary<string, string>? environment = null, IPowerShellWasmDomHost? domHost = null)
    {
        _environment = environment is null ? new(StringComparer.OrdinalIgnoreCase) : new(environment, StringComparer.OrdinalIgnoreCase);
        DomHost = domHost;
        InitializeAutomaticVariables();
    }

    public IPowerShellWasmDomHost? DomHost { get; }
    public IReadOnlyList<PowerShellWasmOutputRecord> Records => _output.Where(IsVisibleOutput).Select(FormatRecord).ToArray();
    public IReadOnlyList<string> Output => Records.Select(FormatRecordLine).ToArray();
    public int ErrorCount => _errors.Count;
    internal bool LastCommandSucceeded => _lastCommandSucceeded;

    internal IDisposable WithPipelineStatus(PipelineStatus status)
    {
        var previous = _pipelineStatus;
        _pipelineStatus = status;
        return new ActionScope(() => _pipelineStatus = previous);
    }

    internal void ClearOutput() =>
        _output.Clear();

    public string? GetEnvironmentVariable(string name) =>
        _environment.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

    public void SetEnvironmentVariable(string name, string? value)
    {
        if (value is null)
        {
            _environment.Remove(name);
            return;
        }

        _environment[name] = value;
    }

    public object? GetVariable(string name) =>
        PowerShellWasmOutput.AsNull(GetRawVariable(name));

    public IReadOnlyDictionary<string, object?> GetVariables() =>
        GetRawVariables().ToDictionary(static item => item.Key, static item => PowerShellWasmOutput.AsNull(item.Value),
            StringComparer.OrdinalIgnoreCase);

    // Keep no-output values internally; callers of the public host API receive null without changing stored state.
    internal object? GetRawVariable(string name)
    {
        name = UnaliasVariableName(name);
        if (name == "?") return _lastCommandSucceeded;
        if (name.Equals("Error", StringComparison.OrdinalIgnoreCase)) return _errors.ToArray();
        for (var frame = _frame; frame is not null; frame = frame.Parent)
        {
            if (frame.Values.TryGetValue(name, out var value)) return value;
        }
        return null;
    }

    internal IReadOnlyDictionary<string, object?> GetRawVariables()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var frames = new Stack<VariableFrame>();
        for (var frame = _frame; frame is not null; frame = frame.Parent) frames.Push(frame);
        foreach (var frame in frames)
            foreach (var item in frame.Values) values[item.Key] = item.Value;
        values["Error"] = _errors.ToArray();
        values["?"] = _lastCommandSucceeded;
        if (values.TryGetValue("_", out var pipelineItem)) values["PSItem"] = pipelineItem;
        return values;
    }

    internal bool HasVariableInCurrentScope(string name)
    {
        name = UnaliasVariableName(name);
        for (var frame = _frame; frame is not null; frame = frame.Parent)
        {
            if (frame.Values.ContainsKey(name)) return true;
            if (!frame.IsOverlay) break;
        }
        return false;
    }

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

    internal int GetNextDomEventRegistrationId() =>
        _nextDomEventRegistrationId++;

    internal PowerShellWasmResult CreateResult(IEnumerable<object?> output) =>
        new(output.Where(IsVisibleOutput).Select(FormatRecord).ToArray());

    public void SetVariable(string name, object? value)
    {
        AssertWritableVariable(name);
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            SetEnvironmentVariable(name[4..], value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        SetFrameVariable(_frame, name, value);
    }

    internal object? AssignVariable(string name, object? value)
    {
        AssertWritableVariable(name);
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) { SetVariable(name, value); return value; }
        return SetFrameVariable(_frame, name, value);
    }

    public void ClearVariable(string name)
    {
        AssertWritableVariable(name);
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            SetEnvironmentVariable(name[4..], null);
            return;
        }

        SetFrameVariable(_frame, name, null);
    }

    public void RemoveVariable(string name)
    {
        AssertWritableVariable(name);
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            SetEnvironmentVariable(name[4..], null);
            return;
        }

        name = UnaliasVariableName(name);
        var frame = GetAssignmentFrame(_frame, name);
        frame.Values.Remove(name);
        frame.Constraints.Remove(name);
    }

    // Type-constrained assignments replace attributes only after successful conversion; ordinary writes reuse them.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/VariableOps.cs#L17-L120
    internal object? SetTypedVariable(string name, object? value, IReadOnlyList<string> typeNames)
    {
        AssertWritableVariable(name);
        name = UnaliasVariableName(name);
        var frame = GetAssignmentFrame(_frame, name);
        if (frame.OptimizedTypes.TryGetValue(name, out var localType))
        {
            value = PowerShellWasmAstExecutor.ConvertOptimizedValue(localType, value, OutputFieldSeparator);
            frame.Values[name] = value;
            return value;
        }
        var constraint = new VariableConstraint(typeNames, []);
        value = constraint.Convert(value, OutputFieldSeparator);
        frame.Values[name] = value;
        frame.Constraints[name] = constraint;
        return value;
    }

    internal void SetParameterConstraints(IReadOnlyList<ParameterDeclarationAst> parameters)
    {
        foreach (var parameter in parameters)
        {
            var name = UnaliasVariableName(parameter.Name);
            GetAssignmentFrame(_frame, name).Constraints[name] = VariableConstraint.ForParameter(parameter);
        }
    }

    internal void SetParameterVariable(ParameterDeclarationAst parameter, object? value, bool retainConstraint)
    {
        AssertWritableVariable(parameter.Name);
        var name = UnaliasVariableName(parameter.Name);
        var frame = GetAssignmentFrame(_frame, name);
        var existed = frame.Values.ContainsKey(name);
        if (frame.OptimizedTypes.TryGetValue(name, out var localType))
            value = PowerShellWasmAstExecutor.ConvertOptimizedValue(localType, value, OutputFieldSeparator);
        frame.Values[name] = value;
        frame.Constraints.Remove(name);
        if (retainConstraint || !existed) frame.Constraints[name] = VariableConstraint.ForParameter(parameter);
    }

    internal void ClearParameterConstraints(IEnumerable<string> names)
    {
        foreach (var parameterName in names)
        {
            var name = UnaliasVariableName(parameterName);
            GetAssignmentFrame(_frame, name).Constraints.Remove(name);
        }
    }

    private static void AssertWritableVariable(string name)
    {
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/QuestionMarkVariable.cs#L17-L39
        if (name == "?") throw new InvalidOperationException("Cannot overwrite variable ? because it is read-only.");
    }

    internal void SetFunction(PowerShellWasmScriptFunction function) =>
        GetScopeFrame(_frame).Functions[function.Name] = function;

    internal bool TryGetFunction(string name, out PowerShellWasmScriptFunction function)
    {
        for (var frame = _frame; frame is not null; frame = frame.Parent)
            if (frame.Functions.TryGetValue(name, out function!)) return true;
        function = null!;
        return false;
    }

    internal IEnumerable<string> GetFunctionNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var frame = _frame; frame is not null; frame = frame.Parent) names.UnionWith(frame.Functions.Keys);
        return names;
    }

    internal IDisposable WithScriptScope(IReadOnlyDictionary<string, object?> variables,
        IReadOnlyDictionary<string, string>? optimizedTypes = null)
    {
        return PushVariableFrame(variables, isOverlay: false, optimizedTypes);
    }

    internal IDisposable WithTemporaryVariables(IReadOnlyDictionary<string, object?> variables)
    {
        return PushVariableFrame(variables, isOverlay: true);
    }

    internal IDisposable WithPipelineItem(object? value)
    {
        return WithTemporaryVariables(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["_"] = value, ["PSItem"] = value
        });
    }

    // Catch saves/restores the current exception independently of pipeline activation and $_.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L5255-L5294
    internal IDisposable WithCaughtException(Exception error)
    {
        var previous = _currentException;
        _currentException = error;
        var original = RecordException(error);
        var record = new Dictionary<string, object?>(original, StringComparer.OrdinalIgnoreCase);
        var identity = _errorExceptions.GetValue(original, _ => new(error, error, IsException: false));
        _errorExceptions.Add(record, identity with { Error = error, IsException = false });
        var variables = WithPipelineItem(record);
        return new ActionScope(() => { variables.Dispose(); _currentException = previous; });
    }

    internal Exception? CurrentException => _currentException;

    internal bool TryGetErrorException(object value, out Exception error)
    {
        error = null!;
        if (!_errorExceptions.TryGetValue(value, out var identity)) return false;
        error = identity.Error;
        if (identity.IsException)
        {
            // Throwing an exception constructs a new record/id; throwing an ErrorRecord preserves its id.
            // Both retain the same exception identity, including head-only history deduplication.
            var original = (Dictionary<string, object?>)error.Data[ErrorRecordDataKey]!;
            error = new InvalidOperationException(error.Message);
            var record = new Dictionary<string, object?>(original, StringComparer.OrdinalIgnoreCase)
            {
                ["FullyQualifiedErrorId"] = identity.ThrowId
            };
            error.Data[ErrorRecordDataKey] = record;
            _errorExceptions.Add(record, identity with { Error = error, IsException = false });
        }
        return true;
    }

    // Throw accepts the original error/exception, or creates a RuntimeException for the converted value.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/Operations/MiscOps.cs#L2079-L2120
    internal Exception CreateThrowException(string message)
    {
        var error = new InvalidOperationException(message);
        CreateErrorRecord(error, "RuntimeException", message, "RuntimeException");
        return error;
    }

    public void WriteOutput(object? value)
    {
        if (PowerShellWasmOutput.IsNoOutput(PowerShellWasmPipelineValue.Unwrap(value))) return;
        WriteToPortSynchronously(_outputPort, value);
    }

    public ValueTask WriteOutputAsync(object? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PowerShellWasmOutput.IsNoOutput(PowerShellWasmPipelineValue.Unwrap(value))
            ? ValueTask.CompletedTask : WriteToPortAsync(_outputPort, value, cancellationToken);
    }

    internal async ValueTask WriteExpressionOutputAsync(object? value, CancellationToken cancellationToken)
    {
        foreach (var item in PowerShellWasmOutput.Enumerate(value)) await WriteOutputAsync(item, cancellationToken);
    }

    // Write-Host uses Information records tagged PSHOST: display bypasses SilentlyContinue, but Ignore and Stop still apply.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L740-L870
    // Browser-safe adaptation: emit an Information text record without native console colors or interactive host prompts.
    public void WriteHost(object? value)
    {
        var record = CreateHostRecord(value, out var preference);
        WriteOutput(record);
        ThrowForStoppingPreference(preference, "InformationPreference", record.Value);
    }

    public async ValueTask WriteHostAsync(object? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = CreateHostRecord(value, out var preference);
        await WriteOutputAsync(record, cancellationToken);
        ThrowForStoppingPreference(preference, "InformationPreference", record.Value);
    }

    private PowerShellWasmStreamRecord CreateHostRecord(object? value, out string preference)
    {
        preference = GetPreferenceValue("InformationPreference", "SilentlyContinue");
        var message = Convert.ToString(PowerShellWasmOutput.AsNull(value), CultureInfo.InvariantCulture) ?? string.Empty;
        var visible = !preference.Equals("Ignore", StringComparison.OrdinalIgnoreCase);
        return new PowerShellWasmStreamRecord("Information", message, visible);
    }

    public void WriteStream(string streamName, object? value)
    {
        var record = CreateStreamRecord(streamName, value, out var preference);
        if (record is not null) WriteOutput(record);
        ThrowForStoppingPreference(preference, GetStreamPreferenceName(streamName), value);
    }

    public async ValueTask WriteStreamAsync(string streamName, object? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = CreateStreamRecord(streamName, value, out var preference);
        if (record is not null) await WriteOutputAsync(record, cancellationToken);
        ThrowForStoppingPreference(preference, GetStreamPreferenceName(streamName), value);
    }

    private PowerShellWasmStreamRecord? CreateStreamRecord(string streamName, object? value, out string preference)
    {
        preference = "Continue";
        if (value is null) return null;
        var preferenceName = GetStreamPreferenceName(streamName);
        preference = preferenceName is null ? "Continue" : GetPreferenceValue(preferenceName, "Continue");
        if (preference.Equals("Ignore", StringComparison.OrdinalIgnoreCase))
        {
            if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                MarkPipelineFailed();
            }
            return HasCommonStreamVariable(streamName) ? new PowerShellWasmStreamRecord(streamName, value, IsVisible: false) : null;
        }

        if (preference.Equals("SilentlyContinue", StringComparison.OrdinalIgnoreCase))
        {
            if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                RecordError(value);
            }

            return HasCommonStreamVariable(streamName) ? new PowerShellWasmStreamRecord(streamName, value, IsVisible: false) : null;
        }

        if (streamName.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            var errorRecord = RecordError(value);
            if (IsStoppingPreference(preference))
            {
                // A caught ActionPreferenceStop exposes the original error once, without writing it to the error pipe.
                // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L2892-L2918
                var error = CreateStoppingPreferenceException(preference, preferenceName, value);
                error.Data[ErrorRecordDataKey] = errorRecord;
                throw error;
            }
        }

        return new PowerShellWasmStreamRecord(streamName, value);
    }

    private static void ThrowForStoppingPreference(string preference, string? preferenceName, object? value)
    {
        if (IsStoppingPreference(preference))
        {
            throw CreateStoppingPreferenceException(preference, preferenceName, value);
        }
    }

    private static InvalidOperationException CreateStoppingPreferenceException(string preference, string? preferenceName, object? value)
    {
        var message = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return new InvalidOperationException(
            $"The running command stopped because the preference variable \"{preferenceName}\" is set to {preference}: {message}");
    }

    // $? belongs to the execution context, not to a function's variable frame.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ExecutionContext.cs#L958
    internal void SetLastCommandSucceeded(bool succeeded) => _lastCommandSucceeded = succeeded;

    // A cmdlet error fails its owning pipeline even when display/history are suppressed with Ignore.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L2863-L2876
    private void MarkPipelineFailed()
    {
        if (_pipelineStatus is not null) _pipelineStatus.Failed = true;
        else SetLastCommandSucceeded(false);
    }

    internal string OutputFieldSeparator =>
        GetPreferenceValue("OFS", " ");

    internal Dictionary<string, object?> RecordException(Exception error)
    {
        var record = error.Data[ErrorRecordDataKey] as Dictionary<string, object?> ??
            CreateErrorRecord(error, error.GetType().Name, error.GetType().FullName ?? error.GetType().Name);
        // Only the latest error is deduplicated. An older error rethrown after another error returns to the head.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ExecutionContext.cs#L833-L857
        if (_errors.Count > 0 && _errors[0] is Dictionary<string, object?> head &&
            (ReferenceEquals(head, record) || _errorExceptions.TryGetValue(head, out var headIdentity) &&
                _errorExceptions.TryGetValue(record, out var recordIdentity) &&
                ReferenceEquals(headIdentity.ExceptionIdentity, recordIdentity.ExceptionIdentity))) return record;
        PushErrorRecord(record, error.ToString());
        return record;
    }

    internal IDisposable CaptureOutput(List<object?> output, bool captureStreams = true)
    {
        var previous = _outputPort;
        _outputPort = new OutputPort(previous, output, captureStreams, null);
        return new OutputScope(this, previous);
    }

    internal async ValueTask WriteCapturedOutputAsync(IEnumerable<object?> output, CancellationToken cancellationToken)
    {
        foreach (var item in output)
        {
            await WriteOutputAsync(item, cancellationToken);
        }
    }

    internal IDisposable RouteOutput(Func<object?, CancellationToken, ValueTask> route)
    {
        var previous = _outputPort;
        _outputPort = new OutputPort(previous, null, true, route);
        return new OutputScope(this, previous);
    }

    internal OutputPort? CurrentOutputPort => _outputPort;

    internal ValueTask WriteToPortAsync(OutputPort? port, object? value, CancellationToken cancellationToken)
    {
        for (; port is not null; port = port.Parent)
        {
            if (port.Output is not null)
            {
                if (!port.CaptureStreams && IsStreamRecord(value)) continue;
                port.Output.Add(value);
                return ValueTask.CompletedTask;
            }
            if (port.Route is not null) return port.Route(value, cancellationToken);
        }
        _output.Add(value);
        return ValueTask.CompletedTask;
    }

    private void WriteToPortSynchronously(OutputPort? port, object? value)
    {
        for (; port is not null; port = port.Parent)
        {
            if (port.Output is not null)
            {
                if (!port.CaptureStreams && IsStreamRecord(value)) continue;
                port.Output.Add(value);
                return;
            }
            if (port.Route is not null)
                throw new InvalidOperationException("Incremental pipeline producers must await WriteOutputAsync; synchronous commands use the buffered adapter.");
        }
        _output.Add(value);
    }

    internal static bool IsStreamRecord(object? value) => PowerShellWasmPipelineValue.Unwrap(value) is PowerShellWasmStreamRecord;

    internal static bool TryGetStreamRecord(object? value, out string streamName, out object? streamValue)
    {
        if (PowerShellWasmPipelineValue.Unwrap(value) is PowerShellWasmStreamRecord stream)
        {
            streamName = stream.StreamName;
            streamValue = stream.Value;
            return true;
        }
        streamName = string.Empty;
        streamValue = null;
        return false;
    }

    internal IReadOnlyList<object?> GetCapturedOutput(IEnumerable<object?> output) =>
        output.Select(PowerShellWasmPipelineValue.Unwrap)
            .Where(static item => item is not PowerShellWasmStreamRecord && !PowerShellWasmOutput.IsNoOutput(item))
            .ToArray();

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

    // WarningVariable/InformationVariable collect records even when the corresponding display is suppressed.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L716-L725
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L860-L870
    private static bool HasCommonStreamVariable(string streamName) =>
        streamName.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
        streamName.Equals("Information", StringComparison.OrdinalIgnoreCase);

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

    private Dictionary<string, object?> RecordError(object? value)
    {
        MarkPipelineFailed();
        var message = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var record = CreateErrorRecord(new InvalidOperationException(message), "Error", "PSWasm.WriteError", message);
        PushErrorRecord(record, null);
        return record;
    }

    private void PushErrorRecord(Dictionary<string, object?> record, string? stackTrace)
    {
        _errors.Insert(0, record);
        SetVariable("StackTrace", stackTrace);
        for (var observer = _errorObserver; observer is not null; observer = observer.Parent) observer.Observe(record);
    }

    // Behavioral guidance: ErrorRecord.Exception.Message in PowerShell's ErrorPackage.cs.
    // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/ErrorPackage.cs
    // Expose a small data descriptor, not a reflected CLR exception; retain Message for existing browser consumers.
    private Dictionary<string, object?> CreateErrorRecord(Exception error, string exception, string fullyQualifiedErrorId,
        string? exceptionThrowId = null)
    {
        var descriptor = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Message"] = error.Message, ["TypeName"] = exception
        };
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Message"] = error.Message,
            ["Exception"] = descriptor,
            ["FullyQualifiedErrorId"] = fullyQualifiedErrorId
        };
        error.Data[ErrorRecordDataKey] = record;
        // Identity is private metadata: user-created lookalike dictionaries cannot impersonate exceptions.
        _errorExceptions.Add(record, new(error, error, IsException: false));
        _errorExceptions.Add(descriptor, new(error, error, IsException: true, exceptionThrowId ?? error.Message));
        return record;
    }

    private sealed record ErrorIdentity(Exception Error, object ExceptionIdentity, bool IsException, string? ThrowId = null);

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

    // A suspended producer retains its frames and output port while a downstream command resumes its caller.
    // Behavioral guidance: PowerShell's Pipe and command processor lifecycle; this adaptation is awaited and single-threaded.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/Pipe.cs
    internal Activation CaptureActivation() => new(_frame, _outputPort, _errorObserver, _scriptCallerFrame, _pipelineStatus);

    internal IDisposable Activate(Activation activation)
    {
        var previous = CaptureActivation();
        _frame = activation.Frame;
        _outputPort = activation.Output;
        _errorObserver = activation.Errors;
        _scriptCallerFrame = activation.ScriptCallerFrame;
        _pipelineStatus = activation.Status;
        return new ActionScope(() =>
        {
            _frame = previous.Frame;
            _outputPort = previous.Output;
            _errorObserver = previous.Errors;
            _scriptCallerFrame = previous.ScriptCallerFrame;
            _pipelineStatus = previous.Status;
        });
    }

    internal IDisposable WithScriptCaller(Activation caller)
    {
        var previous = _scriptCallerFrame;
        _scriptCallerFrame = caller.Frame;
        return new ActionScope(() => _scriptCallerFrame = previous);
    }

    internal IDisposable EnterScriptCaller()
    {
        var previous = _frame;
        _frame = _scriptCallerFrame ?? _frame;
        return new ActionScope(() => _frame = previous);
    }

    internal IDisposable ObserveErrors(Action<object?> observe)
    {
        var previous = _errorObserver;
        _errorObserver = new ErrorObserver(previous, observe);
        return new ActionScope(() => _errorObserver = previous);
    }

    internal void SetVariable(Activation activation, string name, object? value)
    {
        AssertWritableVariable(name);
        SetFrameVariable(activation.Frame, name, value);
    }

    private IDisposable PushVariableFrame(IReadOnlyDictionary<string, object?> variables, bool isOverlay,
        IReadOnlyDictionary<string, string>? optimizedTypes = null)
    {
        var previous = _frame;
        _frame = new VariableFrame(previous, isOverlay);
        foreach (var item in variables) _frame.Values[UnaliasVariableName(item.Key)] = item.Value;
        if (optimizedTypes is not null)
            foreach (var item in optimizedTypes) _frame.OptimizedTypes[UnaliasVariableName(item.Key)] = item.Value;
        return new ActionScope(() => _frame = previous);
    }

    private static VariableFrame GetScopeFrame(VariableFrame frame)
    {
        while (frame.IsOverlay && frame.Parent is not null) frame = frame.Parent;
        return frame;
    }

    private static VariableFrame GetAssignmentFrame(VariableFrame frame, string name)
    {
        while (frame.IsOverlay && !frame.Values.ContainsKey(name) && frame.Parent is not null) frame = frame.Parent;
        return frame;
    }

    // $_ and $PSItem are names for the same slot, including parameter frames and variable cmdlets.
    // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/runtime/MutableTuple.cs#L80-L135
    private static string UnaliasVariableName(string name) =>
        name.Equals("PSItem", StringComparison.OrdinalIgnoreCase) ? "_" : name;

    private object? SetFrameVariable(VariableFrame frame, string name, object? value)
    {
        name = UnaliasVariableName(name);
        frame = GetAssignmentFrame(frame, name);
        if (frame.OptimizedTypes.TryGetValue(name, out var localType))
        {
            value = PowerShellWasmAstExecutor.ConvertOptimizedValue(localType, value, OutputFieldSeparator);
            frame.Values[name] = value;
            return value;
        }
        var originalValue = value;
        if (frame.Constraints.TryGetValue(name, out var constraint)) value = constraint.Convert(value, OutputFieldSeparator);
        frame.Values[name] = value;
        return originalValue;
    }

    private static bool IsVisibleOutput(object? value) =>
        PowerShellWasmPipelineValue.Unwrap(value) is not PowerShellWasmStreamRecord { IsVisible: false } &&
        !PowerShellWasmOutput.IsNoOutput(PowerShellWasmPipelineValue.Unwrap(value));

    private static PowerShellWasmOutputRecord FormatRecord(object? value) =>
        PowerShellWasmPipelineValue.Unwrap(value) switch
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
            PowerShellWasmPipelineValue pipelineValue => FormatOutput(pipelineValue.Value),
            PowerShellWasmStreamRecord stream => FormatOutput(stream.Value),
            System.Collections.Hashtable hashtable => FormatHashtable(hashtable),
            System.Collections.Specialized.OrderedDictionary ordered => FormatHashtable(ordered),
            System.Collections.DictionaryEntry entry => FormatHashtableRows([(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, FormatOutput(entry.Value))]),
            PowerShellWasmCustomObject customObject => FormatHashtable((IReadOnlyDictionary<string, object?>)customObject),
            Dictionary<string, object?> hashtable => "@{" + string.Join("; ", hashtable.Select(static item => $"{item.Key}={FormatOutput(item.Value)}")) + "}",
            object?[] array => string.Join(Environment.NewLine, array.Select(FormatOutput)),
            _ => value.ToString() ?? string.Empty
        };

    private static string FormatHashtable(IReadOnlyDictionary<string, object?> hashtable) =>
        FormatHashtableRows(hashtable.Select(static item => (Name: item.Key, Value: FormatOutput(item.Value))).ToArray());

    private static string FormatHashtable(System.Collections.IDictionary hashtable) =>
        FormatHashtableRows(hashtable.Cast<System.Collections.DictionaryEntry>()
            .Select(static item => (Name: Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? string.Empty, Value: FormatOutput(item.Value)))
            .ToArray());

    private static string FormatHashtableRows((string Name, string Value)[] rows)
    {
        if (rows.Length == 0)
        {
            return string.Empty;
        }

        var nameWidth = Math.Max("Name".Length, rows.Max(static row => row.Name.Length));
        var valueWidth = Math.Max("Value".Length, rows.Max(static row => row.Value.Length));

        var lines = new List<string>
        {
            JoinHashtableCells("Name", "Value", nameWidth, valueWidth),
            JoinHashtableCells("----", "-----", nameWidth, valueWidth)
        };
        lines.AddRange(rows.Select(row => JoinHashtableCells(row.Name, row.Value, nameWidth, valueWidth)));
        return string.Join(Environment.NewLine, lines);
    }

    private static string JoinHashtableCells(string name, string value, int nameWidth, int valueWidth) =>
        $"{name.PadRight(nameWidth)}  {value.PadRight(valueWidth)}".TrimEnd();

    private sealed record PowerShellWasmStreamRecord(string StreamName, object? Value, bool IsVisible = true);

    internal sealed class VariableFrame(VariableFrame? parent, bool isOverlay)
    {
        internal VariableFrame? Parent { get; } = parent;
        internal bool IsOverlay { get; } = isOverlay;
        internal Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, VariableConstraint> Constraints { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> OptimizedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, PowerShellWasmScriptFunction> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record VariableConstraint(IReadOnlyList<string> TypeNames, IReadOnlyList<string> ValidValues)
    {
        internal static VariableConstraint ForParameter(ParameterDeclarationAst parameter) =>
            new(parameter.TypeName is null ? [] : [parameter.TypeName], parameter.ValidateSet);

        internal object? Convert(object? value, string separator)
        {
            foreach (var typeName in TypeNames.Reverse())
                value = PowerShellWasmAstExecutor.ConvertConstrainedValue(typeName, value, separator);
            PowerShellWasmAstExecutor.ValidateConstrainedValue(ValidValues, value);
            return value;
        }
    }

    internal sealed record OutputPort(OutputPort? Parent, List<object?>? Output, bool CaptureStreams,
        Func<object?, CancellationToken, ValueTask>? Route);
    internal sealed record ErrorObserver(ErrorObserver? Parent, Action<object?> Observe);
    internal sealed class PipelineStatus
    {
        internal bool Failed { get; set; }
    }

    internal sealed record Activation(VariableFrame Frame, OutputPort? Output, ErrorObserver? Errors,
        VariableFrame? ScriptCallerFrame, PipelineStatus? Status);
    private sealed class OutputScope(PowerShellWasmExecutionContext context, OutputPort? previous) : IDisposable
    {
        public void Dispose() => context._outputPort = previous;
    }
    private sealed class ActionScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal sealed record PowerShellWasmScriptFunction(
    string Name,
    IReadOnlyList<ParameterDeclarationAst> Parameters,
    ScriptAst Body)
{
    private IReadOnlyDictionary<string, string>? _localTypes;
    internal IReadOnlyDictionary<string, string> LocalTypes =>
        _localTypes ??= PowerShellWasmVariableAnalysis.Analyze(Body, Parameters);
}
