using System.Text.RegularExpressions;
using PSWasm;

internal static class StatusVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell 7.6.1 outcomes cover status separately from retained errors and emitted streams.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, string? ErrorPattern)[]
        {
            ("direct automatic status interpolation observes preceding command", """
"initial=$?"
Write-Error 'direct-error'
"afterError=$?"
"afterString=$?"
""", [new("Output", "initial=True"), new("Error", "direct-error"), new("Output", "afterError=False"), new("Output", "afterString=True")], null),
            ("reading status is itself a successful expression", """
Write-Error 'read-error'
$?
$?
""", [new("Error", "read-error"), new("Output", "False"), new("Output", "True")], null),
            ("braced and adjacent automatic status interpolation", """
Write-Error 'braced-error'
"braced=${?}|adjacent=$?$?|suffix=$?suffix"
""", [new("Error", "braced-error"), new("Output", "braced=False|adjacent=FalseFalse|suffix=Falsesuffix")], null),
            ("subexpression interpolation can change status for later interpolation slots", """
Write-Error 'interpolation-error'
"before=$?|inside=$($?)|after=$?"
""", [new("Error", "interpolation-error"), new("Output", "before=False|inside=False|after=True")], null),
            ("assigning status captures the old value and succeeds", """
Write-Error 'saved-error'
$saved=$?
"saved=$saved|current=$?"
""", [new("Error", "saved-error"), new("Output", "saved=False|current=True")], null),
            ("parenthesized command capture preserves its failing status", """
$value=(Write-Error 'paren-error')
$saved=$?
"saved=$saved|null=$($null -eq $value)"
""", [new("Error", "paren-error"), new("Output", "saved=False|null=True")], null),
            ("subexpression capture follows the last executed statement", """
$value=$(Write-Error 'subexpression-error';'value')
$saved=$?
"saved=$saved|value=$value"
""", [new("Error", "subexpression-error"), new("Output", "saved=True|value=value")], null),
            ("array capture follows the last executed statement", """
$value=@(Write-Error 'array-error';'value')
$saved=$?
"saved=$saved|count=$($value.Count)|value=$($value[0])"
""", [new("Error", "array-error"), new("Output", "saved=True|count=1|value=value")], null),
            ("error-only subexpression and array captures remain failed", """
$value=$(Write-Error 'subexpression-only')
$subStatus=$?
$value=@(Write-Error 'array-only')
$arrayStatus=$?
"sub=$subStatus|array=$arrayStatus|count=$($value.Count)"
""", [new("Error", "subexpression-only"), new("Error", "array-only"), new("Output", "sub=False|array=False|count=0")], null),
            ("a nested error used as a successful command argument does not fail that command", """
Write-Output $(Write-Error 'argument-error';'value')
$saved=$?
"saved=$saved"
""", [new("Error", "argument-error"), new("Output", "value"), new("Output", "saved=True")], null),
            ("ordinary function errors do not directly fail the function invocation", """
function Emit-Error {Write-Error 'function-error'}
Emit-Error
"function=$?"
Emit-Error && 'function-and' || 'function-or'
""", [new("Error", "function-error"), new("Output", "function=True"), new("Error", "function-error"), new("Output", "function-and")], null),
            ("function body can observe failure while its caller sees success", """
function Report-Error {Write-Error 'inner-error';"inside=$?"}
Report-Error
"outside=$?"
""", [new("Error", "inner-error"), new("Output", "inside=False"), new("Output", "outside=True")], null),
            ("call-operator script block errors do not fail the outer invocation", """
& {Write-Error 'scriptblock-error'}
"outside=$?"
& {Write-Error 'scriptblock-value-error';'value'}
"withValue=$?"
""", [new("Error", "scriptblock-error"), new("Output", "outside=True"), new("Error", "scriptblock-value-error"), new("Output", "value"), new("Output", "withValue=True")], null),
            ("ForEach callback errors retain error records without failing its invocation", """
1,2 | ForEach-Object {Write-Error "foreach-$_";"inside=$?"}
"outside=$?"
""", [new("Error", "foreach-1"), new("Output", "inside=False"), new("Error", "foreach-2"), new("Output", "inside=False"), new("Output", "outside=True")], null),
            ("ForEach lifecycle callback errors do not become invocation failure", """
1 | ForEach-Object -Begin {Write-Error 'begin-error'} -Process {Write-Error 'process-error';$_} -End {Write-Error 'end-error'}
"outside=$?"
""", [new("Error", "begin-error"), new("Error", "process-error"), new("Output", "1"), new("Error", "end-error"), new("Output", "outside=True")], null),
            ("a failing upstream command fails the whole pipeline", """
Write-Error 'upstream-error' | ForEach-Object -Process {$_} -End {'downstream-end'}
"outside=$?"
""", [new("Error", "upstream-error"), new("Output", "downstream-end"), new("Output", "outside=False")], null),
            ("a failing downstream command fails the whole pipeline", """
'downstream-error' | Write-Error
"outside=$?"
""", [new("Error", "downstream-error"), new("Output", "outside=False")], null),
            ("a nested pipeline failure inside a callback stays local", """
1 | ForEach-Object {Write-Error 'nested-error' | Write-Output;"nested=$?";$_}
"outer=$?"
""", [new("Error", "nested-error"), new("Output", "nested=False"), new("Output", "1"), new("Output", "outer=True")], null),
            ("caught terminating errors expose failure at catch entry and retain Error", """
$before=$Error.Count
try {throw 'caught-error'} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("try finally reflects the executed cleanup statements", """
try {Write-Error 'try-error'} finally {'cleanup'}
"after=$?"
""", [new("Error", "try-error"), new("Output", "cleanup"), new("Output", "after=True")], null),
            ("an empty catch retains the terminating error status", """
$before=$Error.Count
try {throw 'empty-catch'} catch {}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "after=False|errors=1")], null),
            ("caught failure followed by finally success leaves successful status", """
$before=$Error.Count
try {throw 'catch-finally-error'} catch {"catch=$?"} finally {"finally=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "finally=True"), new("Output", "after=True|errors=1")], null),
            ("catching expression errors follows the same status rules", """
$before=$Error.Count
try {1/0} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("or recovery makes the pipeline chain succeed", """
Write-Error 'recover-error' || 'recovered'
"chain=$?"
""", [new("Error", "recover-error"), new("Output", "recovered"), new("Output", "chain=True")], null),
            ("a skipped and clause preserves the failing pipeline status", """
Write-Error 'skip-error' && 'unexpected'
"chain=$?"
""", [new("Error", "skip-error"), new("Output", "chain=False")], null),
            ("a skipped or clause preserves the successful pipeline status", """
'value' || 'unexpected'
"chain=$?"
""", [new("Output", "value"), new("Output", "chain=True")], null),
            ("pipeline chain decisions use status rather than output truthiness", """
$false && 'false-is-success'
$null && 'null-is-success'
@() && 'empty-is-success'
""", [new("Output", "False"), new("Output", "false-is-success"), new("Output", ""), new("Output", "null-is-success"), new("Output", "empty-is-success")], null),
            ("mixed pipeline chains update status after each executed branch", """
Write-Error 'mixed-error' && 'unexpected' || 'recovered' && 'after-recovery'
"chain=$?"
""", [new("Error", "mixed-error"), new("Output", "recovered"), new("Output", "after-recovery"), new("Output", "chain=True")], null),
            ("a chain assignment captures output and its final status", """
$value=Write-Error 'assigned-chain-error' || 'recovered'
$saved=$?
"saved=$saved|value=$value"
""", [new("Error", "assigned-chain-error"), new("Output", "saved=True|value=recovered")], null),
            ("ErrorAction Ignore fails status without adding an error record", """
$before=$Error.Count
Write-Error 'ignored-error' -ErrorAction Ignore
$saved=$?
"saved=$saved|errors=$($Error.Count-$before)"
""", [new("Output", "saved=False|errors=0")], null),
            ("ErrorAction SilentlyContinue fails status and retains the error", """
$before=$Error.Count
Write-Error 'silent-error' -ErrorAction SilentlyContinue
$saved=$?
"saved=$saved|errors=$($Error.Count-$before)"
""", [new("Output", "saved=False|errors=1")], null),
            ("warning and information streams do not fail commands", """
Write-Warning 'warning'
"warningStatus=$?"
Write-Information 'information' -InformationAction Continue
"informationStatus=$?"
""", [new("Warning", "warning"), new("Output", "warningStatus=True"), new("Information", "information"), new("Output", "informationStatus=True")], null),
            ("WarningAction Stop records a caught failure", """
$before=$Error.Count
try {Write-Warning 'stopping-warning' -WarningAction Stop} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Warning", "stopping-warning"), new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("a normal statement after an error restores success but retains history", """
$before=$Error.Count
Write-Error 'history-error'
$value=42
"after=$?|value=$value|errors=$($Error.Count-$before)"
""", [new("Error", "history-error"), new("Output", "after=True|value=42|errors=1")], null),
            ("uncaught terminating errors remain terminating", """
throw 'uncaught-status-error'
""", [], "uncaught-status-error"),
            ("empty conditional and function definitions preserve previous failure", """
Write-Error 'conditional-error' -ErrorAction SilentlyContinue
if($false){}
"if=$?"
Write-Error 'definition-error' -ErrorAction SilentlyContinue
function Defined-Only {1}
"definition=$?"
""", [new("Output", "if=False"), new("Output", "definition=False")], null),
            ("command conditions reset status after evaluation", """
if((Write-Error 'condition-error' -ErrorAction SilentlyContinue)){}
"after=$?"
if((Write-Error 'operand-error' -ErrorAction SilentlyContinue) -or -not $?) {"inside=$?"}
""", [new("Output", "after=True"), new("Output", "inside=True")], null),
            ("control statements preserve their last executed body status", """
if($true){Write-Error 'body-first' -ErrorAction SilentlyContinue;7}
"lastSuccess=$?"
if($true){8;Write-Error 'body-last' -ErrorAction SilentlyContinue}
"lastFailure=$?"
""", [new("Output", "7"), new("Output", "lastSuccess=True"), new("Output", "8"), new("Output", "lastFailure=False")], null),
            ("empty loops do not overwrite the incoming status", """
Write-Error 'while-error' -ErrorAction SilentlyContinue
while($false){}
"while=$?"
Write-Error 'foreach-empty-error' -ErrorAction SilentlyContinue
foreach($item in @()){}
"foreach=$?"
""", [new("Output", "while=False"), new("Output", "foreach=False")], null),
            ("loop body status survives loop completion", """
foreach($item in 1,2){Write-Error 'loop-body' -ErrorAction SilentlyContinue}
"after=$?"
""", [new("Output", "after=False")], null),
            ("pure computed expressions reset errors in their nested substitutions", """
$value="value=$(Write-Error 'computed-error' -ErrorAction SilentlyContinue)"
"after=$?|value=$value"
$value=1+$(Write-Error 'arithmetic-error' -ErrorAction SilentlyContinue;2)
"arithmetic=$?|value=$value"
""", [new("Output", "after=True|value=value="), new("Output", "arithmetic=True|value=3")], null),
            ("empty subexpression and array expressions restore successful status", """
Write-Error 'empty-subexpression' -ErrorAction SilentlyContinue
$value=$()
"sub=$?|null=$($null -eq $value)"
Write-Error 'empty-array' -ErrorAction SilentlyContinue
$value=@()
"array=$?|count=$($value.Count)"
""", [new("Output", "sub=True|null=True"), new("Output", "array=True|count=0")], null),
            ("producer and downstream callback share current automatic status", """
function Source-Status {Write-Error 'source-error' -ErrorAction SilentlyContinue;1;"producer=$?"}
Source-Status | ForEach-Object {"consumer=$?|value=$_"}
"outside=$?"
""", [new("Output", "consumer=False|value=1"), new("Output", "consumer=True|value=producer=True"), new("Output", "outside=True")], null),
            ("producer expression completion restores success after a callback error", """
function Source-Resume {1;"producer=$?"}
Source-Resume | ForEach-Object {if($_ -eq 1){Write-Error 'callback-error' -ErrorAction SilentlyContinue}else{$_}}
"outside=$?"
""", [new("Output", "producer=True"), new("Output", "outside=True")], null),
            ("ForEach lifecycle blocks observe global status transitions", """
Write-Error 'before-pipeline' -ErrorAction SilentlyContinue
1 | ForEach-Object -Begin {"begin=$?"} -Process {"process=$?";Write-Error 'process-failure' -ErrorAction SilentlyContinue} -End {"end=$?"}
"outside=$?"
""", [new("Output", "begin=False"), new("Output", "process=True"), new("Output", "end=False"), new("Output", "outside=True")], null),
            ("nested argument failures do not suppress successful and branch", """
Write-Output $(Write-Error 'chain-argument' -ErrorAction SilentlyContinue;'argument') && 'accepted'
"after=$?"
""", [new("Output", "argument"), new("Output", "accepted"), new("Output", "after=True")], null),
            ("caught terminating command errors retain history and recover normally", """
$before=$Error.Count
try {Write-Error 'terminating-command' -ErrorAction Stop} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("successful expression interpolation completes after its inner failing command", """
"prefix=$(Write-Error 'interpolated-error' -ErrorAction SilentlyContinue)|inside=$?"
"outside=$?"
""", [new("Output", "prefix=|inside=False"), new("Output", "outside=True")], null),
            ("escaped and single quoted automatic status references stay literal", """
Write-Error 'literal-error' -ErrorAction SilentlyContinue
"escaped=`$?|actual=$?"
'single=$?'
""", [new("Output", "escaped=$?|actual=False"), new("Output", "single=$?")], null),
            ("pipeline failure is published after downstream End", """
Write-Error 'end-timing' -ErrorAction SilentlyContinue | ForEach-Object -Process {} -End {"end=$?"}; "after=$?"
""", [new("Output", "end=True"), new("Output", "after=False")], null),
            ("automatic status rejects direct assignment", """
try {$?=$false} catch {'read-only'}; "after=$?"
""", [new("Output", "read-only"), new("Output", "after=True")], null),
            ("automatic status rejects Set-Variable writes", """
try {Set-Variable -Name '?' -Value $false -ErrorAction Stop} catch {'read-only'}; "after=$?"
""", [new("Output", "read-only"), new("Output", "after=True")], null),
            ("automatic status rejects Clear-Variable", """
try {Clear-Variable -Name '?' -ErrorAction Stop} catch {'read-only'}; "after=$?"
""", [new("Output", "read-only"), new("Output", "after=True")], null),
            ("automatic status rejects Remove-Variable", """
try {Remove-Variable -Name '?' -ErrorAction Stop} catch {'read-only'}; "after=$?"
""", [new("Output", "read-only"), new("Output", "after=True")], null),
            ("condition reset inspects short-circuited commands but excludes script block bodies", """
Write-Error 'short-circuit-command' -ErrorAction SilentlyContinue
if($false -and (Write-Output 1)){}
"command=$?"
Write-Error 'short-circuit-block' -ErrorAction SilentlyContinue
if($false -and {Write-Output 1}){}
"scriptBlock=$?"
""", [new("Output", "command=True"), new("Output", "scriptBlock=False")], null),
            ("while for and do conditions reset status after command evaluation", """
while((Write-Error 'while-condition' -ErrorAction SilentlyContinue)){}
"while=$?"
for(;(Write-Error 'for-condition' -ErrorAction SilentlyContinue);){}
"for=$?"
do{}while((Write-Error 'do-condition' -ErrorAction SilentlyContinue))
"do=$?"
""", [new("Output", "while=True"), new("Output", "for=True"), new("Output", "do=True")], null),
            ("foreach and switch collections preserve failing command status", """
foreach($item in (Write-Error 'foreach-collection' -ErrorAction SilentlyContinue)){}
"foreach=$?"
switch((Write-Error 'switch-collection' -ErrorAction SilentlyContinue)){default{}}
"switch=$?"
""", [new("Output", "foreach=False"), new("Output", "switch=False")], null),
            ("for initializer commands do not receive condition status reset", """
for(Write-Error 'for-initializer' -ErrorAction SilentlyContinue;$false;){}
"initializer=$?"
""", [new("Output", "initializer=False")], null),
            ("condition reset finds commands nested in expandable strings", """
if("$(Write-Error 'string-condition' -ErrorAction SilentlyContinue)" -eq ''){}
"after=$?"
""", [new("Output", "after=True")], null),
            ("ErrorActionPreference Stop catches without emitting an error record twice", """
$before=$Error.Count
$ErrorActionPreference='Stop'
try {Write-Error 'preference-stop';'unexpected'} catch {"catch=$?"}
$ErrorActionPreference='Continue'
"errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "errors=1")], null),
            ("explicit error actions override a stopping preference", """
$before=$Error.Count
$ErrorActionPreference='Stop'
Write-Error 'override-ignore' -ErrorAction Ignore
$ignored=$?
Write-Error 'override-silent' -ErrorAction SilentlyContinue
$silent=$?
Write-Error 'override-continue' -ErrorAction Continue
$continued=$?
$ErrorActionPreference='Continue'
"ignored=$ignored|silent=$silent|continued=$continued|errors=$($Error.Count-$before)"
""", [new("Error", "override-continue"), new("Output", "ignored=False|silent=False|continued=False|errors=2")], null),
            ("terminating callback errors propagate outside the containing pipeline", """
$before=$Error.Count
try {1,2 | ForEach-Object {Write-Error 'callback-stop' -ErrorAction Stop;'unexpected'}} catch {"catch=$?"}
"errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "errors=1")], null),
            ("terminating function errors propagate to their caller", """
$before=$Error.Count
function Fail-Status {Write-Error 'function-stop' -ErrorAction Stop;'unexpected'}
try {Fail-Status} catch {"catch=$?"}
"errors=$($Error.Count-$before)"
""", [new("Output", "catch=False"), new("Output", "errors=1")], null),
            ("InformationAction Stop emits information before terminating", """
$before=$Error.Count
try {Write-Information 'stopping-information' -InformationAction Stop} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Information", "stopping-information"), new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("uncaught stopping commands remain terminating", """
Write-Error 'uncaught-command-stop' -ErrorAction Stop
'unexpected'
""", [], "uncaught-command-stop"),
            ("nested stopping errors retain one history entry through finally", """
$before=$Error.Count
try {try {Write-Error 'nested-stop' -ErrorAction Stop} finally {"finally=$?"}} catch {"catch=$?"}
"after=$?|errors=$($Error.Count-$before)"
""", [new("Output", "finally=False"), new("Output", "catch=False"), new("Output", "after=True|errors=1")], null),
            ("escaped variable and substitution markers remain literal", """
$name='world'
"literal=`$name|actual=$name|braced=`${name}|sub=`$(1+2)"
"even=``$name|odd=```$name"
""", [new("Output", "literal=$name|actual=world|braced=${name}|sub=$(1+2)"), new("Output", "even=`world|odd=`$name")], null),
            ("here strings preserve quoting and expandable escape behavior", "$name='world'\n@\"\nliteral=`$name|actual=$name|tab=`tend\nline=`nnext\n\"@\n@'\nliteral=`$name|actual=$name|tab=`tend\nline=`nnext\n'@", [new("Output", "literal=$name|actual=world|tab=\tend\nline=\nnext"), new("Output", "literal=`$name|actual=$name|tab=`tend\nline=`nnext")], null),
            ("escaped substitution markers do not trigger condition status reset", """
Write-Error 'escaped-condition' -ErrorAction SilentlyContinue
if($false -and "`$(Write-Output 1)"){}
"after=$?"
""", [new("Output", "after=False")], null),
            ("computed member names preserve escaped variable markers", """
$name='other'
$map=@{'$name'=7;other=9}
$map."`$name"
$map."$name"
""", [new("Output", "7"), new("Output", "9")], null),
            ("automatic status rejects common-parameter capture targets", """
try {Write-Output 1 -OutVariable '?'} catch {'out-read-only'}
"out=$?"
try {Write-Output 1 -PipelineVariable '?'} catch {'pipeline-read-only'}
"pipeline=$?"
try {Write-Error 'captured' -ErrorVariable '?' -ErrorAction SilentlyContinue} catch {'error-read-only'}
"error=$?"
""", [new("Output", "out-read-only"), new("Output", "out=True"), new("Output", "pipeline-read-only"), new("Output", "pipeline=True"), new("Output", "error-read-only"), new("Output", "error=True")], null),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected, errorPattern) in cases)
        {
            try
            {
                var runtime = new PowerShellWasmRuntime();
                if (errorPattern is null) AssertRecords(name, await runtime.ExecuteAsync(script), expected);
                else await AssertFailureAsync(name, runtime, script, errorPattern);
            }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        Console.WriteLine($"Automatic status scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static async ValueTask AssertFailureAsync(string name, PowerShellWasmRuntime runtime, string script, string pattern)
    {
        Exception? failure = null;
        try { await runtime.ExecuteAsync(script); }
        catch (Exception exception) { failure = exception; }
        if (failure is null || !Regex.IsMatch(failure.Message, pattern, RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException($"{name}: expected error matching '{pattern}', received '{failure?.Message ?? "no error"}'.");
        }
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




