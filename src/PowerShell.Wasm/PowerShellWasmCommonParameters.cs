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

    public void ApplyCaptures(
        PowerShellWasmExecutionContext executionContext,
        IReadOnlyList<object?> capturedOutput,
        int initialErrorCount)
    {
        SetVariableFromParameter(executionContext, "OutVariable", "ov", executionContext.GetCapturedOutput(capturedOutput));
        SetVariableFromParameter(executionContext, "PipelineVariable", "pv", executionContext.GetCapturedOutput(capturedOutput));
        SetVariableFromParameter(executionContext, "ErrorVariable", "ev", executionContext.GetErrorsAddedAfter(initialErrorCount));
        SetVariableFromParameter(executionContext, "InformationVariable", "iv",
            executionContext.GetCapturedStreamValues(capturedOutput, "Information"));
        SetVariableFromParameter(executionContext, "WarningVariable", "wv",
            executionContext.GetCapturedStreamValues(capturedOutput, "Warning"));
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

    private void SetVariableFromParameter(
        PowerShellWasmExecutionContext executionContext,
        string name,
        string alias,
        IReadOnlyList<object?> values)
    {
        if (!TryGetValue(name, alias, out var variable))
        {
            return;
        }

        var variableName = Convert.ToString(variable, CultureInfo.InvariantCulture) ?? string.Empty;
        executionContext.SetCommonParameterVariable(variableName, values);
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
