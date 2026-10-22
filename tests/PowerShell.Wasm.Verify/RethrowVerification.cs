using System.Text.RegularExpressions;
using PSWasm;

internal static class RethrowVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected outcomes were independently recorded with native PowerShell 7.6.1.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, string? ErrorPattern)[]
        {
            ("bare throw outside catch creates ScriptHalted", """
$before=$Error.Count
try{throw}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)|status=$?"}
""", [new("Output", "message=ScriptHalted|count=1|status=True")], null),
            ("null throw outside catch creates ScriptHalted", """
$before=$Error.Count
try{throw $null}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=1")], null),
            ("empty string throw remains an empty message", """
$before=$Error.Count
try{throw ''}catch{"message=[$($_.Exception.Message)]|count=$($Error.Count-$before)"}
""", [new("Output", "message=[]|count=1")], null),
            ("numeric and boolean throw arguments keep their display values", """
$before=$Error.Count
try{throw 42}catch{"number=$($_.Exception.Message)"}
try{throw $false}catch{"boolean=$($_.Exception.Message)"}
"count=$($Error.Count-$before)"
""", [new("Output", "number=42"), new("Output", "boolean=False"), new("Output", "count=2")], null),
            ("bare rethrow retains original message and exception identity", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;throw}}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|history=$($_.Exception -eq $Error[0].Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|exception=True|history=True|count=1")], null),
            ("explicit catch record retains original message and exception identity", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;throw $_}}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|exception=True|count=1")], null),
            ("explicit caught exception retains original message and identity", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;throw $_.Exception}}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|exception=True|count=1")], null),
            ("stored history record can be thrown after catch ends", """
$before=$Error.Count
try{throw 'stored'}catch{}
$saved=$Error[0]
try{throw $saved}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=stored|exception=True|count=1")], null),
            ("stored history exception can be thrown after catch ends", """
$before=$Error.Count
try{throw 'stored-exception'}catch{}
$saved=$Error[0].Exception
try{throw $saved}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved)|count=$($Error.Count-$before)"}
""", [new("Output", "message=stored-exception|exception=True|count=1")], null),
            ("bare throw after catch ends does not use Error history", """
$before=$Error.Count
try{throw 'old'}catch{}
try{throw}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("null argument inside catch is not bare rethrow", """
$before=$Error.Count
try{try{throw 'original'}catch{throw $null}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("empty argument inside catch replaces the original error", """
$before=$Error.Count
try{try{throw 'original'}catch{throw ''}}catch{"message=[$($_.Exception.Message)]|count=$($Error.Count-$before)"}
""", [new("Output", "message=[]|count=2")], null),
            ("new message in catch creates a replacement history record", """
$before=$Error.Count
try{try{throw 'original'}catch{throw 'replacement'}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=replacement|previous=original|count=2")], null),
            ("rethrowing a stopped command retains its original error record", """
$before=$Error.Count
$saved=$null
try{try{Write-Error 'stopped' -ErrorAction Stop}catch{$saved=$_;throw}}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=stopped|exception=True|count=1")], null),
            ("explicit stopped-command record retains one history entry", """
$before=$Error.Count
try{try{Write-Error 'explicit-stop' -ErrorAction Stop}catch{throw $_}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=explicit-stop|count=1")], null),
            ("throwing a stored nonterminating error retains its message", """
$before=$Error.Count
Write-Error 'nonterminating' -ErrorAction SilentlyContinue
$saved=$Error[0]
try{throw $saved}catch{"message=$($_.Exception.Message)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=nonterminating|exception=True|count=1")], null),
            ("nested catch restores the outer error for bare rethrow", """
$before=$Error.Count
try{try{throw 'outer'}catch{try{throw 'inner'}catch{};throw}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=outer|previous=inner|count=3")], null),
            ("inner rethrow propagates the inner error", """
$before=$Error.Count
try{try{throw 'outer'}catch{try{throw 'inner'}catch{throw}}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=inner|previous=outer|count=2")], null),
            ("restored catch variable selects outer error after nested catch", """
$before=$Error.Count
try{try{throw 'outer'}catch{try{throw 'inner'}catch{};throw $_}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=outer|count=3")], null),
            ("finally executes while a rethrow is propagating", """
$before=$Error.Count
try{try{try{throw 'original'}catch{throw}}finally{'cleanup'}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "cleanup"), new("Output", "message=original|count=1")], null),
            ("bare throw in finally outside catch creates a new error", """
$before=$Error.Count
try{try{throw 'original'}finally{throw}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|previous=original|count=2")], null),
            ("finally inside a catch can rethrow the current error", """
$before=$Error.Count
try{try{throw 'outer'}catch{try{'body'}finally{throw}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "body"), new("Output", "message=outer|count=1")], null),
            ("function defined outside catch does not inherit bare rethrow syntax", """
$before=$Error.Count
function Throw-Outside{throw}
try{try{throw 'original'}catch{Throw-Outside}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("function defined inside catch can rethrow the active error", """
$before=$Error.Count
try{try{throw 'original'}catch{function Throw-Inside{throw};Throw-Inside}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("script block defined outside catch does not inherit bare rethrow syntax", """
$before=$Error.Count
$block={throw}
try{try{throw 'original'}catch{& $block}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("script block defined inside catch rethrows the active error", """
$before=$Error.Count
try{try{throw 'original'}catch{& {throw}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("script block saved inside catch uses no stale exception afterward", """
$before=$Error.Count
$block=$null
try{throw 'old'}catch{$block={throw}}
try{& $block}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("saved rethrow script block uses the exception active at invocation", """
$before=$Error.Count
$block=$null
try{throw 'old'}catch{$block={throw}}
try{try{throw 'current'}catch{& $block}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=current|count=2")], null),
            ("function can explicitly throw a passed error record", """
$before=$Error.Count
function Throw-Record($record){throw $record}
try{try{throw 'original'}catch{Throw-Record $_}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("ForEach callback declared in catch rethrows active error", """
$before=$Error.Count
try{try{throw 'original'}catch{1,2|ForEach-Object {throw}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("saved callback declared outside catch creates ScriptHalted", """
$before=$Error.Count
$block={throw}
try{try{throw 'original'}catch{1|ForEach-Object $block}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("ForEach Begin callback declared in catch rethrows active error", """
$before=$Error.Count
try{try{throw 'original'}catch{1|ForEach-Object -Begin {throw} -Process {$_}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("ForEach End callback declared in catch rethrows active error", """
$before=$Error.Count
try{try{throw 'original'}catch{1|ForEach-Object -Process {$_} -End {throw}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "1"), new("Output", "message=original|count=1")], null),
            ("pipeline input does not become a bare throw argument", """
$before=$Error.Count
try{'pipeline-value' | & {throw}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=1")], null),
            ("bare rethrow uses current exception even when catch variable is overwritten", """
$before=$Error.Count
try{try{throw 'original'}catch{$_='changed';throw}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("explicit overwritten catch variable throws the replacement value", """
$before=$Error.Count
try{try{throw 'original'}catch{$_='changed';throw $_}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=changed|count=2")], null),
            ("throwing an earlier record after a newer error records it again", """
$before=$Error.Count
$saved=$null
try{throw 'first'}catch{$saved=$_}
try{throw 'second'}catch{}
try{throw $saved}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=first|previous=second|count=3")], null),
            ("repeated rethrows of the latest record do not duplicate history", """
$before=$Error.Count
try{try{try{throw 'original'}catch{throw}}catch{throw}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("uncaught bare throw remains terminating", """
throw
""", [], "ScriptHalted"),
            ("uncaught rethrow preserves the original error message", """
try{throw 'uncaught-original'}catch{throw}
""", [], "uncaught-original"),
            ("uncaught explicit error record preserves the original error message", """
try{throw 'uncaught-record'}catch{throw $_}
""", [], "uncaught-record"),
            ("catch wrappers differ from history records but share the same exception", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;"initialRecord=$($_ -eq $Error[0])|initialException=$($_.Exception -eq $Error[0].Exception)";throw}}catch{"record=$($_ -eq $saved)|historyRecord=$($_ -eq $Error[0])|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "initialRecord=False|initialException=True"), new("Output", "record=False|historyRecord=False|exception=True|count=1")], null),
            ("throw arithmetic captures one expression", """
try{throw 1 + 2}catch{$_.Exception.Message}
""", [new("Output", "3")], null),
            ("throw arrays joins values using the output field separator", """
try{throw @('a','b')}catch{"default=$($_.Exception.Message)"}
$OFS=';'
try{throw @('a','b')}catch{"custom=$($_.Exception.Message)"}
""", [new("Output", "default=a b"), new("Output", "custom=a;b")], null),
            ("empty array throw remains an empty message", """
try{throw @()}catch{$_.Exception.Message}
""", [new("Output", "")], null),
            ("throw command expression captures output without emitting it", """
try{throw Write-Output 'captured'}catch{"message=$($_.Exception.Message)"}
""", [new("Output", "message=captured")], null),
            ("throw command expression captures multiple records", """
try{throw Write-Output 'a','b'}catch{"message=$($_.Exception.Message)"}
""", [new("Output", "message=a b")], null),
            ("throw expression discards partial output when evaluation throws", """
function Get-Failure{Write-Output 'partial';throw 'evaluation'}
try{throw Get-Failure}catch{"message=$($_.Exception.Message)"}
""", [new("Output", "message=evaluation")], null),
            ("multiple unseparated throw operands are a parse error", """
throw 'a' 'b'
""", [], "Unexpected token"),
            ("downstream callback rethrows the producer current error during delivery", """
$before=$Error.Count
function Produce-Error{try{throw 'producer'}catch{'go'}}
try{try{throw 'consumer'}catch{Produce-Error | ForEach-Object {throw}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=producer|count=2")], null),
            ("outside callback does not gain rethrow syntax during producer delivery", """
$before=$Error.Count
$block={throw}
function Produce-Error{try{throw 'producer'}catch{'go'}}
try{try{throw 'consumer'}catch{Produce-Error | ForEach-Object $block}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=3")], null),
            ("empty subexpression throw creates ScriptHalted", """
try{throw $()}catch{$_.Exception.Message}
""", [new("Output", "ScriptHalted")], null),
            ("null array elements yield an empty throw message", """
try{throw @($null)}catch{"message=[$($_.Exception.Message)]"}
""", [new("Output", "message=[]")], null),
            ("finally restores the outer current error after a nested catch", """
$before=$Error.Count
try{try{throw 'outer'}catch{try{try{throw 'inner'}catch{}}finally{throw}}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=outer|previous=inner|count=3")], null),
            ("nonterminating error in catch does not replace the current exception", """
$before=$Error.Count
try{try{throw 'original'}catch{Write-Error 'newer' -ErrorAction SilentlyContinue;throw}}catch{"message=$($_.Exception.Message)|previous=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|previous=newer|count=3")], null),
            ("throw array expression retains commas rather than extra command operands", """
try{throw 'a','b'}catch{$_.Exception.Message}
""", [new("Output", "a b")], null),
            ("throw primitives expose native fully qualified error identifiers", """
try{throw 'message'}catch{"message=$($_.FullyQualifiedErrorId)"}
try{throw 0}catch{"zero=$($_.FullyQualifiedErrorId)|message=$($_.Exception.Message)"}
try{throw $false}catch{"false=$($_.FullyQualifiedErrorId)|message=$($_.Exception.Message)"}
try{throw $null}catch{"null=$($_.FullyQualifiedErrorId)"}
try{throw ''}catch{"empty=[$($_.FullyQualifiedErrorId)]"}
try{throw @()}catch{"array=[$($_.FullyQualifiedErrorId)]"}
""", [new("Output", "message=message"), new("Output", "zero=0|message=0"), new("Output", "false=False|message=False"), new("Output", "null=ScriptHalted"), new("Output", "empty=[]"), new("Output", "array=[]")], null),
            ("interpolated subexpression in catch uses lexical bare rethrow", """
$before=$Error.Count
try{try{throw 'original'}catch{"prefix=$(throw)"}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("interpolated nested script block in catch uses lexical bare rethrow", """
$before=$Error.Count
try{try{throw 'original'}catch{"prefix=$(& {throw})"}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("interpolated subexpression in outside function creates ScriptHalted", """
$before=$Error.Count
function Throw-Interpolation{"prefix=$(throw)"}
try{try{throw 'original'}catch{Throw-Interpolation}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("parameter default declared inside catch uses lexical bare rethrow", """
$before=$Error.Count
try{try{throw 'original'}catch{function Throw-Default($value=$(throw)){};Throw-Default}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("parameter default declared outside catch creates ScriptHalted", """
$before=$Error.Count
function Throw-Default($value=$(throw)){}
try{try{throw 'original'}catch{Throw-Default}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=ScriptHalted|count=2")], null),
            ("script block parameter default declared in catch rethrows current error", """
$before=$Error.Count
try{try{throw 'original'}catch{& {param($value=$(throw))}}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|count=1")], null),
            ("throw assignment preserves its assigned value and message", """
$value=0
try{throw $value=1}catch{"message=$($_.Exception.Message)|value=$value"}
""", [new("Output", "message=1|value=1")], null),
            ("unquoted throw operand invokes a command", """
function Hello($value){"command-$value"}
try{throw Hello world}catch{$_.Exception.Message}
""", [new("Output", "command-world")], null),
            ("throwing catch record preserves its error identifier", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;throw $_}}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)|historyId=$($Error[0].FullyQualifiedErrorId)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|id=original|historyId=original|exception=True|count=1")], null),
            ("throwing exception changes catch identifier but preserves history identifier", """
$before=$Error.Count
$saved=$null
try{try{throw 'original'}catch{$saved=$_;throw $_.Exception}}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)|historyId=$($Error[0].FullyQualifiedErrorId)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=original|id=RuntimeException|historyId=original|exception=True|count=1")], null),
            ("record-shaped hashtable remains ordinary thrown data", """
try{throw @{Message='fake';Exception=@{Message='fake'}}}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)"}
""", [new("Output", "message=System.Collections.Hashtable|id=System.Collections.Hashtable")], null),
            ("throwing stopped Write-Error exception keeps its message identifier", """
$before=$Error.Count
$saved=$null
try{try{Write-Error 'stopped-exception' -ErrorAction Stop}catch{$saved=$_;throw $_.Exception}}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)|exception=$($_.Exception -eq $saved.Exception)|count=$($Error.Count-$before)"}
""", [new("Output", "message=stopped-exception|id=stopped-exception|exception=True|count=1")], null),
            ("throwing silent Write-Error exception keeps its message identifier", """
$before=$Error.Count
Write-Error 'silent-exception' -ErrorAction SilentlyContinue
$saved=$Error[0].Exception
try{throw $saved}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)|exception=$($_.Exception -eq $saved)|count=$($Error.Count-$before)"}
""", [new("Output", "message=silent-exception|id=silent-exception|exception=True|count=1")], null),
            ("pending exception returns to history head after finally catches another error", """
$before=$Error.Count
try{try{throw 'first'}finally{try{throw 'second'}catch{}}}catch{"message=$($_.Exception.Message)|head=$($Error[0].Exception.Message)|next=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=first|head=first|next=second|count=3")], null),
            ("pending exception returns to history head after finally writes another error", """
$before=$Error.Count
try{try{throw 'first'}finally{Write-Error 'second' -ErrorAction SilentlyContinue}}catch{"message=$($_.Exception.Message)|head=$($Error[0].Exception.Message)|next=$($Error[1].Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=first|head=first|next=second|count=3")], null),
            ("computed quoted member names preserve lexical bare rethrow", """
$before=$Error.Count
$value=@{}
try{try{throw 'member-original'}catch{$value."$(throw)"}}catch{"message=$($_.Exception.Message)|count=$($Error.Count-$before)"}
""", [new("Output", "message=member-original|count=1")], null),
            ("throwing a custom object preserves its property display", """
try{throw [pscustomobject]@{A=1;B=2}}catch{"message=$($_.Exception.Message)|id=$($_.FullyQualifiedErrorId)"}
""", [new("Output", "message=@{A=1; B=2}|id=@{A=1; B=2}")], null),
            ("custom objects in throw arrays use native element conversion", """
$value=[pscustomobject]@{A=1;B=2}
try{throw @($value,'tail')}catch{"message=$($_.Exception.Message)"}
$OFS=';'
try{throw @($value,'tail')}catch{"custom=$($_.Exception.Message)"}
""", [new("Output", "message= tail"), new("Output", "custom=;tail")], null),
            ("throw captures the result of its entire pipeline chain", """
try{throw 'ChainStop' && Write-Output 'CapturedThrow'}catch{$_.Exception.Message}
""", [new("Output", "ChainStop CapturedThrow")], null),
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
        Console.WriteLine($"Throw and rethrow scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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

