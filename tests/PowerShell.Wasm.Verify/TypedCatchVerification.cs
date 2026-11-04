using PSWasm;

internal static class TypedCatchVerification
{
    public static async ValueTask VerifyAsync()
    {
        VerifyAstCompatibility();
        // Expected records and parser rejections were independently recorded with native PowerShell 7.6.1.
        // The final two cases are browser-runtime metadata trust guards, not native property-mutation claims.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("catch_throw_text_1", """""
try { throw 'typed-original' } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_throw_text_2", """""
try { throw 'typed-original' } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_throw_text_3", """""
try { throw 'typed-original' } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_4", """""
try { throw 'typed-original' } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_5", """""
try { throw 'typed-original' } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_6", """""
try { throw 'typed-original' } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_7", """""
try { throw 'typed-original' } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_8", """""
try { throw 'typed-original' } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_9", """""
try { throw 'typed-original' } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_10", """""
try { throw 'typed-original' } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_throw_text_11", """""
try { throw 'typed-original' } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_12", """""
try { throw 'typed-original' } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_13", """""
try { throw 'typed-original' } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_14", """""
try { throw 'typed-original' } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_15", """""
try { throw 'typed-original' } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_16", """""
try { throw 'typed-original' } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_throw_text_17", """""
try { throw 'typed-original' } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_throw_text_18", """""
try { throw 'typed-original' } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_1", """""
try { 1/0 } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_division_2", """""
try { 1/0 } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_division_3", """""
try { 1/0 } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_4", """""
try { 1/0 } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_5", """""
try { 1/0 } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_6", """""
try { 1/0 } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_division_7", """""
try { 1/0 } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_8", """""
try { 1/0 } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_9", """""
try { 1/0 } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_10", """""
try { 1/0 } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_division_11", """""
try { 1/0 } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_12", """""
try { 1/0 } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_13", """""
try { 1/0 } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_14", """""
try { 1/0 } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_15", """""
try { 1/0 } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_16", """""
try { 1/0 } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_division_17", """""
try { 1/0 } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_division_18", """""
try { 1/0 } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_1", """""
try { [int]'not-a-number' } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_2", """""
try { [int]'not-a-number' } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_3", """""
try { [int]'not-a-number' } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_4", """""
try { [int]'not-a-number' } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_5", """""
try { [int]'not-a-number' } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_6", """""
try { [int]'not-a-number' } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_7", """""
try { [int]'not-a-number' } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_8", """""
try { [int]'not-a-number' } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_9", """""
try { [int]'not-a-number' } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_10", """""
try { [int]'not-a-number' } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_11", """""
try { [int]'not-a-number' } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_12", """""
try { [int]'not-a-number' } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_13", """""
try { [int]'not-a-number' } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_14", """""
try { [int]'not-a-number' } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_15", """""
try { [int]'not-a-number' } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_16", """""
try { [int]'not-a-number' } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_cast_17", """""
try { [int]'not-a-number' } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_cast_18", """""
try { [int]'not-a-number' } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_1", """""
try { 'abc'.Substring(9) } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_2", """""
try { 'abc'.Substring(9) } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_3", """""
try { 'abc'.Substring(9) } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_4", """""
try { 'abc'.Substring(9) } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_5", """""
try { 'abc'.Substring(9) } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_6", """""
try { 'abc'.Substring(9) } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_7", """""
try { 'abc'.Substring(9) } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_8", """""
try { 'abc'.Substring(9) } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_9", """""
try { 'abc'.Substring(9) } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_10", """""
try { 'abc'.Substring(9) } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_11", """""
try { 'abc'.Substring(9) } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_12", """""
try { 'abc'.Substring(9) } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_13", """""
try { 'abc'.Substring(9) } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_14", """""
try { 'abc'.Substring(9) } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_15", """""
try { 'abc'.Substring(9) } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_16", """""
try { 'abc'.Substring(9) } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_substring_17", """""
try { 'abc'.Substring(9) } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_substring_18", """""
try { 'abc'.Substring(9) } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_1", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_command_2", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_command_3", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_4", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_5", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_6", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_7", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_8", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_9", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_10", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_command_11", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_12", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_13", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_command_14", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_15", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_16", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_command_17", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_command_18", """""
try { Invoke-PSWasmTypedCatchMissingCommand } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_1", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_method_2", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_method_3", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_4", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_5", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_6", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_7", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_8", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_9", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_10", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_method_11", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_12", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_13", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_14", """""
try { 'abc'.PSWasmMissingMethod() } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_15", """""
try { 'abc'.PSWasmMissingMethod() } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_16", """""
try { 'abc'.PSWasmMissingMethod() } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_missing_method_17", """""
try { 'abc'.PSWasmMissingMethod() } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_missing_method_18", """""
try { 'abc'.PSWasmMissingMethod() } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_1", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_2", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_3", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_4", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_5", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_6", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_7", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_8", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_9", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_10", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_11", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_12", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_13", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_14", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_15", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_16", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_write_error_17", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_write_error_18", """""
try { Write-Error 'typed-stop' -ErrorAction Stop } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_1", """""
try { 'abc' -match '[' } catch [System.Exception] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_regex_2", """""
try { 'abc' -match '[' } catch [System.SystemException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_regex_3", """""
try { 'abc' -match '[' } catch [System.InvalidOperationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_4", """""
try { 'abc' -match '[' } catch [System.ArgumentException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_regex_5", """""
try { 'abc' -match '[' } catch [System.ArgumentOutOfRangeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_6", """""
try { 'abc' -match '[' } catch [System.DivideByZeroException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_7", """""
try { 'abc' -match '[' } catch [System.FormatException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_8", """""
try { 'abc' -match '[' } catch [System.OverflowException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_9", """""
try { 'abc' -match '[' } catch [System.InvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_10", """""
try { 'abc' -match '[' } catch [System.Management.Automation.RuntimeException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_regex_11", """""
try { 'abc' -match '[' } catch [System.Management.Automation.PSInvalidCastException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_12", """""
try { 'abc' -match '[' } catch [System.Management.Automation.MethodInvocationException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_13", """""
try { 'abc' -match '[' } catch [System.Management.Automation.CommandNotFoundException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_14", """""
try { 'abc' -match '[' } catch [System.Management.Automation.ActionPreferenceStopException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_15", """""
try { 'abc' -match '[' } catch [Microsoft.PowerShell.Commands.WriteErrorException] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_16", """""
try { 'abc' -match '[' } catch [object] { 'matched' } catch { 'fallback' }
""""", [new("Output", "matched")]),
            ("catch_regex_17", """""
try { 'abc' -match '[' } catch [string] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_regex_18", """""
try { 'abc' -match '[' } catch [int] { 'matched' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_syntax_same_filter_same_clause", """""
try { throw 'x' } catch [System.Exception],[System.Exception] { 'matched' }
""""", [new("Output", "matched")]),
            ("catch_syntax_base_and_child_same_clause", """""
try { throw 'x' } catch [System.Exception],[System.ArgumentException] { 'matched' }
""""", [new("Output", "matched")]),
            ("catch_syntax_unknown_filter_quiet", """""
try { 'body' } catch [PSWasm.Unknown.ExceptionType] { 'caught' } finally { 'finally' }
""""", [new("Output", "body"), new("Output", "finally")]),
            ("catch_syntax_unknown_filter_failure", """""
try { try { throw 'x' } catch [PSWasm.Unknown.ExceptionType] { 'caught' } finally { 'finally' } } catch { 'outer' }
""""", [new("Output", "finally"), new("Output", "outer")]),
            ("catch_syntax_nonexception_filter", """""
try { throw 'x' } catch [string] { 'string' } catch { 'fallback' }
""""", [new("Output", "fallback")]),
            ("catch_order_division_runtime_first", """""
try {1/0} catch [System.Management.Automation.RuntimeException] {'runtime'} catch [System.DivideByZeroException] {'division'} catch {'other'}
""""", [new("Output", "division")]),
            ("catch_order_division_inner_first", """""
try {1/0} catch [System.DivideByZeroException] {'division'} catch [System.Management.Automation.RuntimeException] {'runtime'} catch {'other'}
""""", [new("Output", "division")]),
            ("catch_order_substring_wrapper_first", """""
try {'abc'.Substring(9)} catch [System.Management.Automation.MethodInvocationException] {'wrapper'} catch [System.ArgumentOutOfRangeException] {'inner'} catch {'other'}
""""", [new("Output", "inner")]),
            ("catch_order_substring_inner_first", """""
try {'abc'.Substring(9)} catch [System.ArgumentOutOfRangeException] {'inner'} catch [System.Management.Automation.MethodInvocationException] {'wrapper'} catch {'other'}
""""", [new("Output", "inner")]),
            ("catch_order_substring_runtime_first", """""
try {'abc'.Substring(9)} catch [System.Management.Automation.RuntimeException] {'runtime'} catch [System.ArgumentOutOfRangeException] {'inner'} catch {'other'}
""""", [new("Output", "inner")]),
            ("catch_order_substring_argument_first", """""
try {'abc'.Substring(9)} catch [System.ArgumentException] {'argument'} catch [System.Management.Automation.MethodInvocationException] {'wrapper'} catch {'other'}
""""", [new("Output", "argument")]),
            ("catch_order_cast_runtime_first", """""
try {[int]'bad'} catch [System.Management.Automation.RuntimeException] {'runtime'} catch [System.InvalidCastException] {'cast'} catch {'other'}
""""", [new("Output", "cast")]),
            ("catch_order_writeerror_runtime_first", """""
try {Write-Error 'x' -ErrorAction Stop} catch [System.Management.Automation.RuntimeException] {'runtime'} catch [Microsoft.PowerShell.Commands.WriteErrorException] {'writeerror'} catch {'other'}
""""", [new("Output", "runtime")]),
            ("catch_order_writeerror_stop_first", """""
try {Write-Error 'x' -ErrorAction Stop} catch [System.Management.Automation.ActionPreferenceStopException] {'stop'} catch [Microsoft.PowerShell.Commands.WriteErrorException] {'writeerror'} catch {'other'}
""""", [new("Output", "stop")]),
            ("catch_order_writeerror_underlying_first", """""
try {Write-Error 'x' -ErrorAction Stop} catch [Microsoft.PowerShell.Commands.WriteErrorException] {'writeerror'} catch [System.Management.Automation.ActionPreferenceStopException] {'stop'} catch {'other'}
""""", [new("Output", "stop")]),
            ("catch_filter_multiple", """""
try {1/0} catch [System.FormatException], [System.DivideByZeroException] {'match'} catch {'other'}
""""", [new("Output", "match")]),
            ("catch_filter_multiple_no_match", """""
try {throw 'x'} catch [System.FormatException], [System.DivideByZeroException] {'wrong'} catch {'other'}
""""", [new("Output", "other")]),
            ("catch_filter_case_alias", """""
try {throw 'x'} catch [eXCePTion] {'match'}
""""", [new("Output", "match")]),
            ("catch_filter_system_alias", """""
try {throw 'x'} catch [SystemException] {'match'}
""""", [new("Output", "match")]),
            ("catch_filter_array", """""
try {throw 'x'} catch [string[]] {'wrong'} catch {'other'}
""""", [new("Output", "other")]),
            ("catch_filter_interface", """""
try {throw 'x'} catch [System.Runtime.Serialization.ISerializable] {'wrong'} catch {'other'}
""""", [new("Output", "other")]),
            ("catch_filter_generic", """""
try {throw 'x'} catch [System.Collections.Generic.List[string]] {'wrong'} catch {'other'}
""""", [new("Output", "other")]),
            ("catch_filter_unknown_later", """""
try {try {throw 'x'} catch [System.Management.Automation.RuntimeException] {'wrong'} catch [PSWasm.Unknown.ExceptionType] {'wrong2'} finally {'finally'}} catch {'outer'}
""""", [new("Output", "finally"), new("Output", "outer")]),
            ("catch_filter_unknown_same", """""
try {try {throw 'x'} catch [System.Exception], [PSWasm.Unknown.ExceptionType] {'wrong'} finally {'finally'}} catch {'outer'}
""""", [new("Output", "finally"), new("Output", "outer")]),
            ("catch_filter_unknown_quiet_later", """""
try {'body'} catch [System.Management.Automation.RuntimeException] {'wrong'} catch [PSWasm.Unknown.ExceptionType] {'wrong2'} finally {'finally'}
""""", [new("Output", "body"), new("Output", "finally")]),
            ("catch_propagation_no_match", """""
try {try {throw 'original'} catch [System.ArgumentException] {'wrong'} finally {'finally'}} catch [System.Management.Automation.RuntimeException] {$_.Exception.Message}
""""", [new("Output", "finally"), new("Output", "original")]),
            ("catch_propagation_nested_finally", """""
try {try {try {throw 'original'} catch [System.ArgumentException] {'wrong'} finally {'inner-finally'}} finally {'outer-finally'}} catch [System.Exception] {$_.Exception.Message}
""""", [new("Output", "inner-finally"), new("Output", "outer-finally"), new("Output", "original")]),
            ("catch_success_skip_handlers", """""
try {'body'} catch [System.Exception] {'wrong'} finally {'finally'};'after'
""""", [new("Output", "body"), new("Output", "finally"), new("Output", "after")]),
            ("catch_catch_failure_skips_sibling", """""
try {try {throw 'original'} catch [System.Management.Automation.RuntimeException] {throw 'replacement'} catch [System.Exception] {'wrong'} finally {'finally'}} catch {$_.Exception.Message}
""""", [new("Output", "finally"), new("Output", "replacement")]),
            ("catch_finally_replaces", """""
try {try {throw 'original'} catch [System.ArgumentException] {'wrong'} finally {throw 'replacement'}} catch [System.Management.Automation.RuntimeException] {$_.Exception.Message}
""""", [new("Output", "replacement")]),
            ("catch_return_runs_finally", """""
function Invoke-Test {try {return 'value'} catch [System.Exception] {'wrong'} finally {'finally'}};Invoke-Test
""""", [new("Output", "value"), new("Output", "finally")]),
            ("catch_break_runs_finally", """""
foreach($i in 1,2){try {break} catch [System.Exception] {'wrong'} finally {'finally'}};'after'
""""", [new("Output", "finally"), new("Output", "after")]),
            ("catch_continue_runs_finally", """""
foreach($i in 1,2){try {continue} catch [System.Exception] {'wrong'} finally {"finally=$i"}};'after'
""""", [new("Output", "finally=1"), new("Output", "finally=2"), new("Output", "after")]),
            ("catch_catch_scope_restore", """""
$_='before';try {throw 'x'} catch [System.Exception] {$_.Exception.Message};"after=$_"
""""", [new("Output", "x"), new("Output", "after=before")]),
            ("catch_catch_status", """""
try {throw 'x'} catch [System.Exception] {"catch=$?"};"after=$?"
""""", [new("Output", "catch=False"), new("Output", "after=True")]),
            ("catch_catch_pipeline", """""
1 | ForEach-Object {try {throw 'x'} catch [System.Exception] {$_.Exception.Message}}
""""", [new("Output", "x")]),
            ("catch_catch_expression", """""
$(try {throw 'x'} catch [System.Exception] {'selected'})
""""", [new("Output", "selected")]),
            ("catch_catch_assignment", """""
$value=try {throw 'x'} catch [System.Exception] {'selected'};"value=$value"
""""", [new("Output", "value=selected")]),
            ("catch_rethrow_text_bare", """""
$before=$Error.Count;$saved=$null;try {try {throw 'original'} catch [System.Management.Automation.RuntimeException] {$saved=$_;throw}} catch [System.Management.Automation.RuntimeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_rethrow_text_record", """""
$before=$Error.Count;$saved=$null;try {try {throw 'original'} catch [System.Management.Automation.RuntimeException] {$saved=$_;throw $_}} catch [System.Management.Automation.RuntimeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_rethrow_text_exception", """""
$before=$Error.Count;$saved=$null;try {try {throw 'original'} catch [System.Management.Automation.RuntimeException] {$saved=$_;throw $_.Exception}} catch [System.Management.Automation.RuntimeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_rethrow_division_bare", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch [System.DivideByZeroException] {$saved=$_;throw}} catch [System.DivideByZeroException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=False;count=1")]),
            ("catch_rethrow_division_record", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch [System.DivideByZeroException] {$saved=$_;throw $_}} catch [System.DivideByZeroException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_division_exception", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch [System.DivideByZeroException] {$saved=$_;throw $_.Exception}} catch [System.DivideByZeroException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_cast_bare", """""
$before=$Error.Count;$saved=$null;try {try {[int]'bad'} catch [System.InvalidCastException] {$saved=$_;throw}} catch [System.InvalidCastException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=False;count=1")]),
            ("catch_rethrow_cast_record", """""
$before=$Error.Count;$saved=$null;try {try {[int]'bad'} catch [System.InvalidCastException] {$saved=$_;throw $_}} catch [System.InvalidCastException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_cast_exception", """""
$before=$Error.Count;$saved=$null;try {try {[int]'bad'} catch [System.InvalidCastException] {$saved=$_;throw $_.Exception}} catch [System.InvalidCastException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_substring_bare", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch [System.ArgumentOutOfRangeException] {$saved=$_;throw}} catch [System.ArgumentOutOfRangeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=False;count=1")]),
            ("catch_rethrow_substring_record", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch [System.ArgumentOutOfRangeException] {$saved=$_;throw $_}} catch [System.ArgumentOutOfRangeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_substring_exception", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch [System.ArgumentOutOfRangeException] {$saved=$_;throw $_.Exception}} catch [System.ArgumentOutOfRangeException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=2")]),
            ("catch_rethrow_stopped_bare", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch [Microsoft.PowerShell.Commands.WriteErrorException] {$saved=$_;throw}} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_rethrow_stopped_record", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch [Microsoft.PowerShell.Commands.WriteErrorException] {$saved=$_;throw $_}} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_rethrow_stopped_exception", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch [Microsoft.PowerShell.Commands.WriteErrorException] {$saved=$_;throw $_.Exception}} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"match=True;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'wrong'}
""""", [new("Output", "match=True;identity=True;history=True;count=1")]),
            ("catch_boundary_adjacent_string", """""
try {'a'}catch{} 'after'
""""", [new("Output", "a"), new("Output", "after")]),
            ("catch_boundary_adjacent_command", """""
try {'a'}catch{} Write-Output after
""""", [new("Output", "a"), new("Output", "after")]),
            ("catch_boundary_multiline_header", """""
try
{}
catch
[Exception]
{}
finally
{}
""""", []),
            ("catch_boundary_newline_before_comma", """""
try {throw 'x'} catch [System.ArgumentException]
,[System.Exception] {'selected'}
""""", [new("Output", "selected")]),
            ("catch_boundary_newline_after_comma", """""
try {throw 'x'} catch [System.ArgumentException],
[System.Exception] {'selected'}
""""", [new("Output", "selected")]),
            ("catch_boundary_newline_after_type", """""
try {throw 'x'} catch [System.Exception]
{'selected'}
""""", [new("Output", "selected")]),
            ("catch_boundary_newline_before_type", """""
try {throw 'x'} catch
[System.Exception] {'selected'}
""""", [new("Output", "selected")]),
            ("catch_boundary_newline_empty_handler", """""
try
{throw 'x'}
catch
{'selected'}
""""", [new("Output", "selected")]),
            ("catch_boundary_unknown_duplicate", """""
try{}catch [PSWasm.Unknown.ExceptionType]{}catch [PSWasm.Unknown.ExceptionType]{}
""""", []),
            ("catch_boundary_unknown_same_clause", """""
try{}catch [PSWasm.Unknown.ExceptionType],[PSWasm.Unknown.ExceptionType]{}
""""", []),
            ("catch_type_trailing_dot", """""
try {'body'} catch [Exception.] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_double_dot", """""
try {'body'} catch [System..Exception] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_int_array", """""
try {'body'} catch [int[]] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_short_generic", """""
try {'body'} catch [Collections.Generic.List[int]] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_unknown_generic", """""
try {'body'} catch [Missing[int]] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_assembly", """""
try {'body'} catch [Exception,SomeAssembly] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_multiline_start", """""
try {'body'} catch [
System.Exception] {'caught'}
""""", [new("Output", "body")]),
            ("catch_type_unknown_duplicate", """""
try {'body'} catch [PSWasm.Unknown.ExceptionType] {} catch [PSWasm.Unknown.ExceptionType] {}
""""", [new("Output", "body")]),
            ("catch_type_unknown_case_duplicate", """""
try {'body'} catch [PSWasm.Unknown.ExceptionType] {} catch [pswasm.unknown.exceptiontype] {}
""""", [new("Output", "body")]),
            ("catch_type_alias_same_clause", """""
try {throw 'x'} catch [Exception],[System.Exception] {'caught'}
""""", [new("Output", "caught")]),
            ("catch_type_interface_then_exception", """""
try {throw 'x'} catch [System.Runtime.Serialization.ISerializable] {'wrong'} catch [System.Exception] {'caught'}
""""", [new("Output", "caught")]),
            ("catch_boundary_catch_spaced_empty", """""
try {} catch [Exception] {}
""""", []),
            ("catch_boundary_adjacent_catch_caught", """""
try {try {'body'} finally {'finally'} catch {'wrong'}} catch [System.Management.Automation.CommandNotFoundException] {'trailing-command'}
""""", [new("Output", "body"), new("Output", "finally"), new("Output", "trailing-command")]),
            ("catch_boundary_adjacent_finally_caught", """""
try {try {'body'} finally {'finally'} finally {'wrong'}} catch [System.Management.Automation.CommandNotFoundException] {'trailing-command'}
""""", [new("Output", "body"), new("Output", "finally"), new("Output", "trailing-command")]),
            ("catch_boundary_separated_catch_caught", """""
try {try {'body'} catch {};catch {'wrong'}} catch [System.Management.Automation.CommandNotFoundException] {'trailing-command'}
""""", [new("Output", "body"), new("Output", "trailing-command")]),
            ("catch_boundary_adjacent_typed_catch_caught", """""
try {try {'body'} finally {'finally'} catch [Exception] {'wrong'}} catch [System.Management.Automation.CommandNotFoundException] {'trailing-command'}
""""", [new("Output", "body"), new("Output", "finally"), new("Output", "trailing-command")]),
            ("catch_cast_overflow", """""
try {[int]2147483648} catch [System.OverflowException] {'overflow'} catch [System.Management.Automation.PSInvalidCastException] {'cast'} catch {'other'}
""""", [new("Output", "cast")]),
            ("catch_decimal_division", """""
try {1d/0d} catch [System.DivideByZeroException] {'division'} catch {'other'}
""""", [new("Output", "division")]),
            ("catch_bigint_division", """""
try {1n/0n} catch [System.DivideByZeroException] {'division'} catch {'other'}
""""", [new("Output", "division")]),
            ("catch_null_method", """""
try {$null.ToString()} catch [System.Management.Automation.RuntimeException] {'runtime'} catch {'other'}
""""", [new("Output", "runtime")]),
            ("catch_parse_datetime", """""
try {[datetime]::Parse('nonsense')} catch [System.FormatException] {'format'} catch [System.Management.Automation.MethodInvocationException] {'method'} catch {'other'}
""""", [new("Output", "format")]),
            ("catch_parse_timespan", """""
try {[timespan]::Parse('nonsense')} catch [System.FormatException] {'format'} catch {'other'}
""""", [new("Output", "format")]),
            ("catch_filter_hierarchy_methods", """""
try {'abc'.Substring(9)} catch [System.Management.Automation.MethodException] {'method'} catch {'other'}
""""", [new("Output", "method")]),
            ("catch_filter_hierarchy_ets", """""
try {'abc'.Substring(9)} catch [System.Management.Automation.ExtendedTypeSystemException] {'ets'} catch {'other'}
""""", [new("Output", "ets")]),
            ("catch_review_command_equals_try", """""
function Show-Args {"count=$($args.Count)"}; Show-Args x=try {} catch {} 'after'
""""", [new("Output", "count=5")]),
            ("catch_review_command_spaced_equals_try", """""
function Show-Args {"count=$($args.Count)"}; Show-Args x = try {} catch {} 'after'
""""", [new("Output", "count=7")]),
            ("catch_review_command_equals_try_finally", """""
function Show-Args {"count=$($args.Count)"}; Show-Args x=try {} finally {} 'after'
""""", [new("Output", "count=5")]),
            ("catch_review_assignment_newline_rhs", """""
$value=
try {'body'} catch {} 'after';"value=$value"
""""", [new("Output", "after"), new("Output", "value=body")]),
            ("catch_review_assignment_typed", """""
[string]$value=try {'body'} catch {} 'after';"value=$value"
""""", [new("Output", "after"), new("Output", "value=body")]),
            ("catch_review_assignment_member", """""
$o=[pscustomobject]@{Value='old'};$o.Value=try {'body'} catch {} 'after';$o.Value
""""", [new("Output", "after"), new("Output", "body")]),
            ("catch_review_assignment_index", """""
$a=@('old');$a[0]=try {'body'} catch {} 'after';$a[0]
""""", [new("Output", "after"), new("Output", "body")]),
            ("catch_review_assignment_parallel", """""
$a,$b=try {1;2} catch {} 'after';"a=$a;b=$b"
""""", [new("Output", "after"), new("Output", "a=1;b=2")]),
            ("catch_review_assignment_discard", """""
$null=try {'body'} catch {} 'after'
""""", [new("Output", "after")]),
            ("catch_review_type_spaced_before_bracket", """""
try {'body'} catch [Exception ] {}
""""", [new("Output", "body")]),
            ("catch_review_type_spaced_start", """""
try {'body'} catch [ Exception] {}
""""", [new("Output", "body")]),
            ("catch_review_type_whitespace_array", """""
try {'body'} catch [int []] {}
""""", [new("Output", "body")]),
            ("catch_review_type_whitespace_array_inner", """""
try {'body'} catch [int[ ]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_generic_whitespace", """""
try {'body'} catch [Collections.Generic.List[ int ]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_generic_space_before", """""
try {'body'} catch [Collections.Generic.List [int]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_generic_dictionary", """""
try {'body'} catch [Collections.Generic.Dictionary[string,int]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_generic_nested", """""
try {'body'} catch [Collections.Generic.List[Collections.Generic.List[int]]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_multidimensional", """""
try {'body'} catch [int[,]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_array_of_array", """""
try {'body'} catch [int[][]] {}
""""", [new("Output", "body")]),
            ("catch_review_type_plus_tail", """""
try {'body'} catch [Exception+] {}
""""", [new("Output", "body")]),
            ("catch_review_type_assembly_version", """""
try {'body'} catch [Exception, SomeAssembly, Version=1.0.0.0] {}
""""", [new("Output", "body")]),
            ("catch_review_type_nonascii", """""
try {'body'} catch [ΣException] {}
""""", [new("Output", "body")]),
            ("catch_review_type_namespace_number", """""
try {'body'} catch [Exception.1] {}
""""", [new("Output", "body")]),
            ("catch_review_type_nested_number", """""
try {'body'} catch [Exception+1] {}
""""", [new("Output", "body")]),
            ("catch_review_type_unknown_nested", """""
try {'body'} catch [Missing+Nested] {}
""""", [new("Output", "body")]),
            ("catch_review_catch_comment_header", """""
try {throw 'x'} catch <# header #> [Exception] {'caught'}
""""", [new("Output", "caught")]),
            ("catch_review_catch_comment_comma", """""
try {throw 'x'} catch [System.ArgumentException] <# comma #>, [Exception] {'caught'}
""""", [new("Output", "caught")]),
            ("catch_review_untyped_rethrow_division_bare", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch {$saved=$_;throw}} catch [System.DivideByZeroException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.RuntimeException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=specific;type=System.DivideByZeroException;identity=False;history=False;count=1")]),
            ("catch_review_untyped_rethrow_division_record", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch {$saved=$_;throw $_}} catch [System.DivideByZeroException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.RuntimeException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=wrapper;type=System.Management.Automation.RuntimeException;identity=True;history=True;count=1")]),
            ("catch_review_untyped_rethrow_division_exception", """""
$before=$Error.Count;$saved=$null;try {try {1/0} catch {$saved=$_;throw $_.Exception}} catch [System.DivideByZeroException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.RuntimeException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=specific;type=System.DivideByZeroException;identity=False;history=False;count=1")]),
            ("catch_review_descriptor_division_specific", """""
try {1/0} catch [System.DivideByZeroException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.DivideByZeroException]);wrapper=$($_.Exception -is [System.Management.Automation.RuntimeException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.DivideByZeroException;history=System.Management.Automation.RuntimeException;specific=True;wrapper=False;exception=True")]),
            ("catch_review_descriptor_division_wrapper", """""
try {1/0} catch [System.Management.Automation.RuntimeException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.DivideByZeroException]);wrapper=$($_.Exception -is [System.Management.Automation.RuntimeException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.Management.Automation.RuntimeException;history=System.Management.Automation.RuntimeException;specific=False;wrapper=True;exception=True")]),
            ("catch_review_descriptor_division_all", """""
try {1/0} catch [System.Exception] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.DivideByZeroException]);wrapper=$($_.Exception -is [System.Management.Automation.RuntimeException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.DivideByZeroException;history=System.Management.Automation.RuntimeException;specific=True;wrapper=False;exception=True")]),
            ("catch_review_untyped_rethrow_substring_bare", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch {$saved=$_;throw}} catch [System.ArgumentOutOfRangeException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.MethodInvocationException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=specific;type=System.ArgumentOutOfRangeException;identity=False;history=False;count=1")]),
            ("catch_review_untyped_rethrow_substring_record", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch {$saved=$_;throw $_}} catch [System.ArgumentOutOfRangeException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.MethodInvocationException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=wrapper;type=System.Management.Automation.MethodInvocationException;identity=True;history=True;count=1")]),
            ("catch_review_untyped_rethrow_substring_exception", """""
$before=$Error.Count;$saved=$null;try {try {'abc'.Substring(9)} catch {$saved=$_;throw $_.Exception}} catch [System.ArgumentOutOfRangeException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [System.Management.Automation.MethodInvocationException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=specific;type=System.ArgumentOutOfRangeException;identity=False;history=False;count=1")]),
            ("catch_review_descriptor_substring_specific", """""
try {'abc'.Substring(9)} catch [System.ArgumentOutOfRangeException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.ArgumentOutOfRangeException]);wrapper=$($_.Exception -is [System.Management.Automation.MethodInvocationException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.ArgumentOutOfRangeException;history=System.Management.Automation.MethodInvocationException;specific=True;wrapper=False;exception=True")]),
            ("catch_review_descriptor_substring_wrapper", """""
try {'abc'.Substring(9)} catch [System.Management.Automation.MethodInvocationException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.ArgumentOutOfRangeException]);wrapper=$($_.Exception -is [System.Management.Automation.MethodInvocationException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.Management.Automation.MethodInvocationException;history=System.Management.Automation.MethodInvocationException;specific=False;wrapper=True;exception=True")]),
            ("catch_review_descriptor_substring_all", """""
try {'abc'.Substring(9)} catch [System.Exception] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.ArgumentOutOfRangeException]);wrapper=$($_.Exception -is [System.Management.Automation.MethodInvocationException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=System.ArgumentOutOfRangeException;history=System.Management.Automation.MethodInvocationException;specific=True;wrapper=False;exception=True")]),
            ("catch_review_untyped_rethrow_stopped_bare", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch {$saved=$_;throw}} catch [System.Management.Automation.ActionPreferenceStopException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=specific;type=Microsoft.PowerShell.Commands.WriteErrorException;identity=True;history=True;count=1")]),
            ("catch_review_untyped_rethrow_stopped_record", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch {$saved=$_;throw $_}} catch [System.Management.Automation.ActionPreferenceStopException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=wrapper;type=Microsoft.PowerShell.Commands.WriteErrorException;identity=True;history=True;count=1")]),
            ("catch_review_untyped_rethrow_stopped_exception", """""
$before=$Error.Count;$saved=$null;try {try {Write-Error 'original' -ErrorAction Stop} catch {$saved=$_;throw $_.Exception}} catch [System.Management.Automation.ActionPreferenceStopException] {"selected=specific;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"selected=wrapper;type=$($_.Exception.GetType().FullName);identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"} catch {'other'}
""""", [new("Output", "selected=wrapper;type=Microsoft.PowerShell.Commands.WriteErrorException;identity=True;history=True;count=1")]),
            ("catch_review_descriptor_stopped_specific", """""
try {Write-Error 'original' -ErrorAction Stop} catch [System.Management.Automation.ActionPreferenceStopException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.Management.Automation.ActionPreferenceStopException]);wrapper=$($_.Exception -is [Microsoft.PowerShell.Commands.WriteErrorException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=Microsoft.PowerShell.Commands.WriteErrorException;history=Microsoft.PowerShell.Commands.WriteErrorException;specific=False;wrapper=True;exception=True")]),
            ("catch_review_descriptor_stopped_wrapper", """""
try {Write-Error 'original' -ErrorAction Stop} catch [Microsoft.PowerShell.Commands.WriteErrorException] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.Management.Automation.ActionPreferenceStopException]);wrapper=$($_.Exception -is [Microsoft.PowerShell.Commands.WriteErrorException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=Microsoft.PowerShell.Commands.WriteErrorException;history=Microsoft.PowerShell.Commands.WriteErrorException;specific=False;wrapper=True;exception=True")]),
            ("catch_review_descriptor_stopped_all", """""
try {Write-Error 'original' -ErrorAction Stop} catch [System.Exception] {"selected=$($_.Exception.GetType().FullName);history=$($Error[0].Exception.GetType().FullName);specific=$($_.Exception -is [System.Management.Automation.ActionPreferenceStopException]);wrapper=$($_.Exception -is [Microsoft.PowerShell.Commands.WriteErrorException]);exception=$($_.Exception -is [System.Exception])"}
""""", [new("Output", "selected=Microsoft.PowerShell.Commands.WriteErrorException;history=Microsoft.PowerShell.Commands.WriteErrorException;specific=False;wrapper=True;exception=True")]),
            ("catch_final_generic_array_valid", """""
try {'body'} catch [Collections.Generic.List[int][]] {}
""""", [new("Output", "body")]),
            ("catch_final_iterator_list_movenext", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.MoveNext()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_list_movenext_wrapper", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.MoveNext()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_list_reset", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.Reset()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_list_reset_wrapper", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.Reset()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_arraylist_movenext", """""
$items=[System.Collections.ArrayList]@(1,2);$iterator=$items.GetEnumerator();[void]$items.Add(3);try{$iterator.MoveNext()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_arraylist_movenext_wrapper", """""
$items=[System.Collections.ArrayList]@(1,2);$iterator=$items.GetEnumerator();[void]$items.Add(3);try{$iterator.MoveNext()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_arraylist_reset", """""
$items=[System.Collections.ArrayList]@(1,2);$iterator=$items.GetEnumerator();[void]$items.Add(3);try{$iterator.Reset()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_arraylist_reset_wrapper", """""
$items=[System.Collections.ArrayList]@(1,2);$iterator=$items.GetEnumerator();[void]$items.Add(3);try{$iterator.Reset()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_dictionary_movenext", """""
$items=@{a=1;b=2};$iterator=$items.GetEnumerator();$items.c=3;try{$iterator.MoveNext()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_dictionary_movenext_wrapper", """""
$items=@{a=1;b=2};$iterator=$items.GetEnumerator();$items.c=3;try{$iterator.MoveNext()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_dictionary_reset", """""
$items=@{a=1;b=2};$iterator=$items.GetEnumerator();$items.c=3;try{$iterator.Reset()}catch [System.InvalidOperationException]{'inner'}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_dictionary_reset_wrapper", """""
$items=@{a=1;b=2};$iterator=$items.GetEnumerator();$items.c=3;try{$iterator.Reset()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch{'other'}
""""", [new("Output", "wrapper")]),
            ("catch_final_iterator_list_movenext_precedence", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.MoveNext()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch [System.InvalidOperationException]{'inner'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_list_reset_precedence", """""
$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{$iterator.Reset()}catch [System.Management.Automation.MethodInvocationException]{'wrapper'}catch [System.InvalidOperationException]{'inner'}catch{'other'}
""""", [new("Output", "inner")]),
            ("catch_final_iterator_rethrow_bare", """""
$before=$Error.Count;$saved=$null;$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{try{$iterator.MoveNext()}catch [System.InvalidOperationException]{$saved=$_;throw}}catch [System.InvalidOperationException]{"inner;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"}catch{'other'}
""""", [new("Output", "inner;identity=True;history=False;count=1")]),
            ("catch_final_iterator_rethrow_record", """""
$before=$Error.Count;$saved=$null;$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{try{$iterator.MoveNext()}catch [System.InvalidOperationException]{$saved=$_;throw $_}}catch [System.InvalidOperationException]{"inner;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"}catch{'other'}
""""", [new("Output", "inner;identity=True;history=True;count=2")]),
            ("catch_final_iterator_rethrow_exception", """""
$before=$Error.Count;$saved=$null;$items=[System.Collections.Generic.List[int]]@(1,2);$iterator=$items.GetEnumerator();$items.Add(3);try{try{$iterator.MoveNext()}catch [System.InvalidOperationException]{$saved=$_;throw $_.Exception}}catch [System.InvalidOperationException]{"inner;identity=$($_.Exception -eq $saved.Exception);history=$($_.Exception -eq $Error[0].Exception);count=$($Error.Count-$before)"}catch{'other'}
""""", [new("Output", "inner;identity=True;history=True;count=2")]),
            ("catch_final_assembly_plus_quiet", """""
try {'body'} catch [Exception,+] {'wrong'}
""""", [new("Output", "body")]),
            ("catch_final_assembly_minus_quiet", """""
try {'body'} catch [Exception,-] {'wrong'}
""""", [new("Output", "body")]),
            ("catch_final_assembly_comma_quiet", """""
try {'body'} catch [Exception,,] {'wrong'}
""""", [new("Output", "body")]),
            ("catch_final_assembly_numeric_quiet", """""
try {'body'} catch [Exception,1] {'wrong'}
""""", [new("Output", "body")]),
            ("catch_final_unsupported_generic_later_quiet", """""
try {'body'} catch [System.ArgumentException] {} catch [System.Collections.Generic.List[System.Guid]] {}
""""", [new("Output", "body")]),
            ("catch_final_unknown_generic_later_throws", """""
try {try {throw 'original'} catch [System.ArgumentException] {'wrong'} catch [System.Collections.Generic.List[PSWasm.Unknown.Type]] {'wrong2'} finally {'finally'}} catch {'outer'}
""""", [new("Output", "finally"), new("Output", "outer")]),
            ("catch_runtime_mutated_descriptor_classification", """""
$saved=$null;try{1/0}catch [System.DivideByZeroException]{$saved=$_;$saved.Exception.TypeName="System.ArgumentException";$saved.Exception.Message="tampered"};try{throw $saved}catch [System.DivideByZeroException]{"division;identity=$($_.Exception -eq $saved.Exception)"}catch{"wrong"}
""""", [new("Output", "division;identity=True")]),
            ("catch_runtime_forged_descriptor_is_not_exception", """""
$fake=@{Exception=@{TypeName="System.DivideByZeroException";Message="fake"};FullyQualifiedErrorId="fake"};try{throw $fake}catch [System.DivideByZeroException]{"wrong"}catch [System.Management.Automation.RuntimeException]{"runtime"}
""""", [new("Output", "runtime")]),
        };
        var diagnostics = new (string Name, string Script, string NativeIds, string Pattern)[]
        {
            ("catch_syntax_missing_handler", """""
try { 'body' }
""""", "MissingCatchOrFinally", "catch|finally|handler"),
            ("catch_syntax_missing_try_body", """""
try catch {}
""""", "MissingTryStatementBlock", "try|block|\\{"),
            ("catch_syntax_bare_catch_word", """""
try {} catch word {}
""""", "MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_empty_filter", """""
try {} catch [] {}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_filter_no_close", """""
try {} catch [System.Exception {}
""""", "EndSquareBracketExpectedAtEndOfAttribute", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_filter_no_body", """""
try {} catch [System.Exception]
""""", "MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_filter_no_comma", """""
try {} catch [System.Exception][System.ArgumentException] {}
""""", "MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_filter_trailing_comma", """""
try {} catch [System.Exception], {}
""""", "MissingTypeLiteralToken", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_filter_leading_comma", """""
try {} catch ,[System.Exception] {}
""""", "MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_syntax_catchall_then_typed", """""
try {} catch {} catch [System.Exception] {}
""""", "EmptyCatchNotLast", "catch.all|last.*catch|catch.*last"),
            ("catch_syntax_catchall_twice", """""
try {} catch {} catch {}
""""", "EmptyCatchNotLast", "catch.all|last.*catch|catch.*last"),
            ("catch_syntax_same_type_twice", """""
try {} catch [System.Exception] {} catch [System.Exception] {}
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_syntax_base_then_child", """""
try {} catch [System.Exception] {} catch [System.ArgumentException] {}
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_syntax_object_then_exception", """""
try {} catch [object] {} catch [System.Exception] {}
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_syntax_finally_with_type", """""
try {} finally [int] {}
""""", "MissingFinallyStatementBlock", "finally|block"),
            ("catch_boundary_semicolon_before_handler", """""
try {};catch {}
""""", "MissingCatchOrFinally", "catch|finally|handler"),
            ("catch_boundary_semicolon_after_filter", """""
try {} catch [System.Exception];{}
""""", "MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_boundary_attribute_filter", """""
try {} catch [System.Exception()] {}
""""", "MissingCatchHandlerBlock,UnexpectedAttribute", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_spaced_identifiers", """""
try {'body'} catch [Exception Exception] {'caught'}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_spaced_dots", """""
try {'body'} catch [System . Exception] {'caught'}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_number", """""
try {'body'} catch [1] {'caught'}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_empty", """""
try {'body'} catch [] {'caught'}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_multiline_end", """""
try {'body'} catch [System.Exception
] {'caught'}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_multiline_dot", """""
try {'body'} catch [System.
Exception] {'caught'}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_type_alias_duplicate", """""
try {'body'} catch [Exception] {} catch [System.Exception] {}
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_type_nonexception_duplicate", """""
try {} catch [int] {} catch [System.Int32] {}
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_boundary_catch_no_space", """""
try {} catch[Exception] {}
""""", "MissingCatchOrFinally", "catch|finally|handler"),
            ("catch_filter_type_syntax_unreachable", """""
if($false){try {} catch [Exception] {} catch [System.ArgumentException] {}};'after'
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_filter_type_syntax_interpolation", """""
"$(try {} catch [Exception] {} catch [System.ArgumentException] {})"
""""", "ExceptionTypeAlreadyCaught", "already.*(caught|handled)|previous.*catch|exception.*type"),
            ("catch_review_type_identifier_number", """""
try {'body'} catch [Exception 1] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock,UnexpectedToken", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_number_identifier", """""
try {'body'} catch [Ex1 ception] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_generic_comma_tail", """""
try {'body'} catch [Collections.Generic.List[int,]] {}
""""", "MissingTypename", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_generic_no_comma", """""
try {'body'} catch [Collections.Generic.Dictionary[string int]] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_plus_start", """""
try {'body'} catch [+Exception] {}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_star", """""
try {'body'} catch [Exception*] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_ampersand", """""
try {'body'} catch [Exception&] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_question", """""
try {'body'} catch [Exception?] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_nested_empty", """""
try {'body'} catch [Exception[[]]] {}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_trailing_generic_text", """""
try {'body'} catch [Collections.Generic.List[int]oops] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_review_type_quoted", """""
try {'body'} catch ['Exception'] {}
""""", "MissingTypename,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_final_generic_after_generic", """""
try {} catch [Collections.Generic.List[int][string]] {}
""""", "UnexpectedToken", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_final_generic_after_array", """""
try {} catch [int[][string]] {}
""""", "UnexpectedToken", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_final_assembly_missing_name", """""
try {} catch [Exception,=] {}
""""", "MissingAssemblyNameSpecification,EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
            ("catch_final_assembly_spaced_name", """""
try {} catch [Exception,Some Assembly] {}
""""", "EndSquareBracketExpectedAtEndOfAttribute,MissingCatchHandlerBlock", "catch|type|bracket|block|\\]|\\{|token"),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected) in cases)
        {
            try
            {
                var runtime = new PowerShellWasmRuntime();
                AssertRecords(name, await runtime.ExecuteAsync(script), expected);
            }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        Console.WriteLine($"Typed catch scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        var outputFailures = failures.Count;
        foreach (var (name, script, nativeIds, pattern) in diagnostics)
        {
            Exception? failure = null;
            try { new PSWasm.Language.PowerShellWasmParser().Parse(script); }
            catch (Exception exception) { failure = exception; }
            if (failure is not InvalidOperationException ||
                !System.Text.RegularExpressions.Regex.IsMatch(failure.Message, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                failures.Add($"{name}: expected parse rejection matching {pattern} (native: {nativeIds}); " +
                    $"received {failure?.GetType().Name ?? "no exception"}: {failure?.Message}");
            }
        }
        Console.WriteLine($"Typed catch diagnostics: {diagnostics.Length - (failures.Count - outputFailures)}/{diagnostics.Length} passed.");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    private static void VerifyAstCompatibility()
    {
        var parser = new PSWasm.Language.PowerShellWasmParser();
        var parsed = (PSWasm.Language.TryStatementAst)parser.Parse(
            "try { throw 'value' } catch [System.Exception] { 'previous' }").Statements.Single();
        var replacement = parser.Parse("'replacement'");
        var rewritten = parsed with { CatchBlocks = new[] { replacement } };
        if (!ReferenceEquals(rewritten.CatchClauses.Single().Body, replacement) ||
            rewritten.CatchClauses.Single().TypeNames.Single() != "System.Exception")
            throw new InvalidOperationException("Updating CatchBlocks must retain filters and replace the derived clause body.");
        var (_, bodies, _) = rewritten;
        if (!ReferenceEquals(bodies.Single(), replacement))
            throw new InvalidOperationException("TryStatementAst deconstruction must retain its original catch-body shape.");
        var mutableBodies = new List<PSWasm.Language.ScriptAst> { parser.Parse("'original'") };
        var legacy = new PSWasm.Language.TryStatementAst(parsed.TryBlock, mutableBodies, null);
        mutableBodies[0] = replacement;
        if (!ReferenceEquals(legacy.CatchClauses.Single().Body, replacement) ||
            legacy.CatchClauses.Single().TypeNames.Count != 0)
            throw new InvalidOperationException("Legacy catch-body collections must remain authoritative for untyped AST construction.");
        _ = new PSWasm.Language.TryStatementAst(parsed.TryBlock, [], null);
        Console.WriteLine("Typed catch AST compatibility: passed.");
    }

    private static void AssertRecords(string name, PowerShellWasmResult result, PowerShellWasmOutputRecord[] expected)
    {
        if (result.Records.SequenceEqual(expected)) return;
        var expectedText = string.Join(Environment.NewLine, expected.Select(static record => $"{record.Stream}: {record.Text}"));
        var actualText = string.Join(Environment.NewLine, result.Records.Select(static record => $"{record.Stream}: {record.Text}"));
        throw new InvalidOperationException(
            $"{name}{Environment.NewLine}Expected records:{Environment.NewLine}{expectedText}" +
            $"{Environment.NewLine}Actual records:{Environment.NewLine}{actualText}");
    }
}
