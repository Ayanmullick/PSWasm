using System.Collections;
using System.Globalization;

namespace PSWasm;

internal sealed class PowerShellWasmCommonParameters
{
    private readonly IReadOnlyDictionary<string, object?> _parameters;
    private readonly Dictionary<string, object?> _preferenceOverrides = new(StringComparer.OrdinalIgnoreCase);

    private PowerShellWasmCommonParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        _parameters = parameters;
        AddSwitchPreference("Debug", "db", "DebugPreference");
        AddSwitchPreference("Verbose", "vb", "VerbosePreference");
        AddActionPreference("ErrorAction", "ea", "ErrorActionPreference");
        AddActionPreference("InformationAction", "infa", "InformationPreference");
        AddActionPreference("ProgressAction", "proga", "ProgressPreference");
        AddActionPreference("WarningAction", "wa", "WarningPreference");

        if (TryGetValue("WhatIf", "wi", out var whatIf))
        {
            _preferenceOverrides["WhatIfPreference"] = ToBoolean(whatIf);
        }
    }

    public static PowerShellWasmCommonParameters From(IReadOnlyDictionary<string, object?> parameters) =>
        new(parameters);

    public IDisposable Apply(PowerShellWasmExecutionContext executionContext) =>
        executionContext.WithTemporaryVariables(_preferenceOverrides);

    public Invocation CreateInvocation(PowerShellWasmExecutionContext context) => new(this, context);

    internal sealed class Invocation : IDisposable
    {
        private readonly PowerShellWasmExecutionContext _context;
        private readonly PowerShellWasmExecutionContext.Activation _caller;
        private readonly IList? _output, _errors, _information, _warnings;
        private readonly string? _pipelineVariable;

        internal Invocation(PowerShellWasmCommonParameters parameters, PowerShellWasmExecutionContext context)
        {
            _context = context;
            _caller = context.CaptureActivation();
            _output = CreateCapture(parameters, "OutVariable", "ov");
            _errors = CreateCapture(parameters, "ErrorVariable", "ev");
            _information = CreateCapture(parameters, "InformationVariable", "iv");
            _warnings = CreateCapture(parameters, "WarningVariable", "wv");
            if (parameters.TryGetValue("PipelineVariable", "pv", out var pipelineVariable))
            {
                _pipelineVariable = Convert.ToString(pipelineVariable, CultureInfo.InvariantCulture)?.TrimStart('+');
                if (!string.IsNullOrWhiteSpace(_pipelineVariable)) context.SetVariable(_caller, _pipelineVariable, null);
            }
        }

        private IList? CreateCapture(PowerShellWasmCommonParameters parameters, string name, string alias)
        {
            if (!parameters.TryGetValue(name, alias, out var variable)) return null;
            var variableName = Convert.ToString(variable, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(variableName)) return null;
            var append = variableName.StartsWith('+');
            if (append) variableName = variableName[1..];
            var existing = append ? _context.GetRawVariable(variableName) : null;
            // SetupVariable reuses an append target's mutable IList so pipeline stages observe the same capture.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/MshCommandRuntime.cs#L2551-L2604
            IList values;
            if (existing is IList list && !list.IsFixedSize)
            {
                values = list;
            }
            else
            {
                var collected = new List<object?>();
                if (existing is IList fixedList)
                    foreach (var item in fixedList) collected.Add(item);
                else if (existing is not null) collected.AddRange(PowerShellWasmOutput.Enumerate(existing));
                values = collected;
            }
            _context.SetVariable(_caller, variableName, values);
            return values;
        }

        internal void ObserveError(object? record) => _errors?.Add(record);

        internal object? ObserveOutput(object? record)
        {
            if (PowerShellWasmExecutionContext.TryGetStreamRecord(record, out var streamName, out var value))
            {
                if (streamName.Equals("Information", StringComparison.OrdinalIgnoreCase)) _information?.Add(value);
                if (streamName.Equals("Warning", StringComparison.OrdinalIgnoreCase)) _warnings?.Add(value);
                return record;
            }

            // Native Pipe.Add collects OutVariable and updates PipelineVariable before delivering the success record.
            // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/Pipe.cs#L393-L425
            value = PowerShellWasmPipelineValue.Unwrap(record);
            _output?.Add(value);
            if (!string.IsNullOrWhiteSpace(_pipelineVariable))
            {
                _context.SetVariable(_caller, _pipelineVariable, value);
            }
            return record;
        }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(_pipelineVariable)) _context.SetVariable(_caller, _pipelineVariable, null);
        }
    }

    private void AddSwitchPreference(string name, string alias, string preferenceName)
    {
        if (TryGetValue(name, alias, out var value))
        {
            _preferenceOverrides[preferenceName] = ToBoolean(value) ? "Continue" : "SilentlyContinue";
        }
    }

    private void AddActionPreference(string name, string alias, string preferenceName)
    {
        if (TryGetValue(name, alias, out var value))
        {
            _preferenceOverrides[preferenceName] = NormalizeActionPreference(value);
        }
    }

    private bool TryGetValue(string name, string alias, out object? value) =>
        _parameters.TryGetValue(name, out value) || _parameters.TryGetValue(alias, out value);

    private static string NormalizeActionPreference(object? value)
    {
        if (value is null or true)
        {
            return "Continue";
        }

        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "SilentlyContinue",
                1 => "Stop",
                2 => "Continue",
                3 => "Inquire",
                4 => "Ignore",
                5 => "Suspend",
                6 => "Break",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Continue"
            };
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Continue";
    }

    private static bool ToBoolean(object? value) =>
        value switch
        {
            null => true,
            bool boolValue => boolValue,
            int intValue => intValue != 0,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            string stringValue => stringValue.Length > 0,
            _ => true
        };
}
