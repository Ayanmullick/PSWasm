using System.Net;
using System.Net.Http;
using PSWasm;

internal static class IncrementalPipelineVerification
{
    public static async ValueTask VerifyAsync()
    {
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("downstream side effects run before the next source statement", """
$state=@{N=0}
function Emit { 1; $state.N }
Emit | ForEach-Object { $state.N=9; $_ }
""", [new("Output", "1"), new("Output", "9")]),
            ("source function locals stay isolated while caller writes survive", """
$Local='caller'
$Downstream='before'
function Emit { param($Local='source'); 1; "source=$Local" }
Emit | ForEach-Object { "item=$_|local=$Local"; $Downstream='after' }
"caller=$Local|downstream=$Downstream"
""", [new("Output", "item=1|local=caller"), new("Output", "item=source=source|local=caller"), new("Output", "caller=caller|downstream=after")]),
            ("Begin Process End execute once in connected stage order", """
$state=@{Log=''}
1 | ForEach-Object -Begin { $state.Log+='A' } -Process { $state.Log+='P'; $_ } -End { $state.Log+='Z' } |
    ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='Q' } -End { $state.Log+='Y' }
$state.Log
""", [new("Output", "ABPQZY")]),
            ("Begin output activates downstream before upstream Begin resumes", """
1 | ForEach-Object -Begin { 'begin-source'; Write-Warning 'begin-warning' } -Process { 'process-source' } |
    ForEach-Object -Begin { 'begin-target' } -Process { "received=$_" }
""", [new("Output", "begin-target"), new("Output", "received=begin-source"), new("Warning", "begin-warning"), new("Output", "received=process-source")]),
            ("empty input still runs Begin and End once without Process", """
@() | ForEach-Object -Begin { 'begin' } -Process { 'unexpected-process' } -End { 'end' } |
    ForEach-Object -Begin { 'target-begin' } -Process { "received=$_" } -End { 'target-end' }
""", [new("Output", "target-begin"), new("Output", "received=begin"), new("Output", "received=end"), new("Output", "target-end")]),
            ("downstream arguments bind once before source execution", """
$state=@{Log=''}
function Emit { $state.Log+='S'; @{V=1}; @{V=2}; $state.Log+='E' }
Emit | Select-Object -ExpandProperty $($state.Log+='A'; 'V') |
    ForEach-Object { $state.Log+="P$_"; $_ }
$state.Log
""", [new("Output", "1"), new("Output", "2"), new("Output", "ASP1P2E")]),
            ("OutVariable and PipelineVariable update before downstream receives each value", """
1,2 | Write-Output -OutVariable seen -PipelineVariable current |
    ForEach-Object { "during:count=$($seen.Count)|item=$_|pv=$current" }
"after:count=$($seen.Count)|pv=$current|null=$($null -eq $current)"
""", [new("Output", "during:count=1|item=1|pv=1"), new("Output", "during:count=2|item=2|pv=2"), new("Output", "after:count=2|pv=|null=True")]),
            ("append OutVariable initializes once for a pipeline invocation", """
$seen=@('old')
1,2 | Write-Output -OutVariable +seen | ForEach-Object { "item=$_|seen=$($seen -join ',')" }
"after=$($seen -join ',')"
""", [new("Output", "item=1|seen=old,1"), new("Output", "item=2|seen=old,1,2"), new("Output", "after=old,1,2")]),
            ("common captures include Begin and End output in order", """
1,2 | ForEach-Object -Begin { 'B' } -Process { $_ } -End { 'E' } -OutVariable seen -PipelineVariable current |
    ForEach-Object { "value=$_|seen=$($seen -join ',')|pv=$current" }
"after=$($seen -join ',')|pv=$current"
""", [new("Output", "value=B|seen=B|pv=B"), new("Output", "value=1|seen=B,1|pv=1"), new("Output", "value=2|seen=B,1,2|pv=2"), new("Output", "value=E|seen=B,1,2,E|pv=E"), new("Output", "after=B,1,2,E|pv=")]),
            ("upstream common preferences do not leak downstream", """
1 | ForEach-Object { Write-Verbose 'upstream-verbose'; Write-Warning 'upstream-warning'; $_ } -Verbose -WarningAction Ignore |
    ForEach-Object { Write-Verbose 'downstream-hidden'; Write-Warning 'downstream-warning'; $_ }
""", [new("Warning", "upstream-warning"), new("Warning", "downstream-warning"), new("Output", "1")]),
            ("ordered side streams remain interleaved across connected stages", """
function Emit { 1; Write-Warning 'source-middle'; 2 }
Emit | ForEach-Object { Write-Verbose "processing=$_" -Verbose; $_ }
""", [new("Verbose", "processing=1"), new("Output", "1"), new("Warning", "source-middle"), new("Verbose", "processing=2"), new("Output", "2")]),
            ("Sort accumulates input before emitting sorted output", """
$state=@{Log=''}
function Emit { $state.Log+='A'; 2; $state.Log+='B'; 1; $state.Log+='C' }
Emit | Sort-Object | ForEach-Object { $state.Log+="P$_"; $_ }
$state.Log
""", [new("Output", "1"), new("Output", "2"), new("Output", "ABCP1P2")]),
            ("Group retains all input across processor calls", """
'x','y','x' | Group-Object | ForEach-Object { "name=$($_.Name)|count=$($_.Count)" }
""", [new("Output", "name=x|count=2"), new("Output", "name=y|count=1")]),
            ("Measure retains count and sum across processor calls", """
1,2,3 | Measure-Object -Sum -Minimum -Maximum |
    ForEach-Object { "count=$($_.Count)|sum=$($_.Sum)|min=$($_.Minimum)|max=$($_.Maximum)" }
""", [new("Output", "count=3|sum=6|min=1|max=3")]),
            ("Select Last uses the complete upstream window", """
1,2,3 | Select-Object -Last 2 | ForEach-Object { "last=$_" }
""", [new("Output", "last=2"), new("Output", "last=3")]),
            ("a plain function consumes its input once", """
function Summarize { $values=@($input); "count=$($values.Count)|values=$($values -join ',')" }
1,2,3 | Summarize
""", [new("Output", "count=3|values=1,2,3")]),
            ("source throw preserves already consumed success and side streams", """
function Emit { 'source-value'; Write-Warning 'source-warning'; throw 'boom' }
try { Emit | ForEach-Object { "processed=$_" } } catch { 'caught-source' }
""", [new("Output", "processed=source-value"), new("Warning", "source-warning"), new("Output", "caught-source")]),
            ("downstream throw stops source before its next statement", """
$state=@{After=0}
function Emit { 'first'; $state.After=1; 'second' }
try { Emit | ForEach-Object { "processed=$_"; throw 'boom' } } catch { 'caught-downstream' }
"source-after=$($state.After)"
""", [new("Output", "processed=first"), new("Output", "caught-downstream"), new("Output", "source-after=0")]),
            ("source End throw preserves End output and skips downstream End", """
try {
    1 | ForEach-Object -Process { $_ } -End { 'end-partial'; throw 'boom' } |
        ForEach-Object -Process { "received=$_" } -End { 'unexpected-end' }
} catch { 'caught-end' }
""", [new("Output", "received=1"), new("Output", "received=end-partial"), new("Output", "caught-end")]),
            ("failed capture forwards side streams without exposing partial success", """
$result='old'
try {
    $result=@(1,2 | ForEach-Object { $_; Write-Warning "warning=$_"; if ($_ -eq 2) { throw 'boom' } })
} catch { 'caught-capture' }
"result=$result"
""", [new("Warning", "warning=1"), new("Warning", "warning=2"), new("Output", "caught-capture"), new("Output", "result=old")]),
            ("OutVariable keeps emitted records when the command throws", """
try {
    1,2 | ForEach-Object { $_; if ($_ -eq 2) { throw 'boom' } } -OutVariable seen |
        ForEach-Object { "received=$_" }
} catch { 'caught' }
"seen=$($seen -join ',')"
""", [new("Output", "received=1"), new("Output", "received=2"), new("Output", "caught"), new("Output", "seen=1,2")]),
            ("nested pipelines restore the outer current item", """
1,2 | ForEach-Object { $outer=$_; 10,20 | ForEach-Object { "nested=$outer/$_" }; "outer=$_" }
""", [new("Output", "nested=1/10"), new("Output", "nested=1/20"), new("Output", "outer=1"), new("Output", "nested=2/10"), new("Output", "nested=2/20"), new("Output", "outer=2")]),
            ("empty stages run lifecycle without fabricating input", """
$state=@{Log=''}
@() | ForEach-Object -Begin { $state.Log+='A' } -Process { $state.Log+='P' } -End { $state.Log+='Z' } |
    ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='Q' } -End { $state.Log+='Y' }
$state.Log
""", [new("Output", "ABZY")]),
            ("Sort evaluates calculated keys after collecting its input", """
$state=@{Key=0; Log=''}
function Emit { $state.Log+='S1;'; $state.Key=2; 'A'; $state.Log+='S2;'; $state.Key=1; 'B'; $state.Log+='S3;'; $state.Key=3 }
Emit | Sort-Object { $state.Log+="K$_=$($state.Key);"; $state.Key } |
    ForEach-Object { $state.Log+="O$_;"; $_ }
$state.Log
""", [new("Output", "A"), new("Output", "B"), new("Output", "S1;S2;S3;KA=3;KB=3;OA;OB;")]),
            ("Group evaluates calculated keys when each input arrives", """
$state=@{Key=0}
function Emit { $state.Key=2; 'A'; $state.Key=1; 'B'; $state.Key=3 }
Emit | Group-Object { $state.Key } | ForEach-Object { "name=$($_.Name)|values=$($_.Group -join ',')" }
""", [new("Output", "name=1|values=B"), new("Output", "name=2|values=A")]),
            ("standalone ForEach runs Process once with null input", """
ForEach-Object -Begin { 'begin' } -Process { "process:null=$($null -eq $_)" } -End { 'end' }
""", [new("Output", "begin"), new("Output", "process:null=True"), new("Output", "end")]),
            ("source function body waits for downstream Begin", """
$state=@{Log=''}
function Emit { $state.Log+='S'; 1 }
Emit | ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='P' }
$state.Log
""", [new("Output", "BSP")]),
            ("source expression evaluation timing", """
$state=@{Log=''}
$($state.Log+='E'; 1) | ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='P' }
$state.Log
""", [new("Output", "EBP")]),
            ("source expression and downstream argument evaluation order", """
$state=@{Log=''}
$($state.Log+='E'; @{V=1}) | Select-Object -ExpandProperty $($state.Log+='A'; 'V') |
    ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='P' }
$state.Log
""", [new("Output", "EABP")]),
            ("three stage Begin output initializes consumers lazily", """
$state=@{Log=''}
1 | ForEach-Object -Begin { $state.Log+='A'; 'x' } -Process { $state.Log+='P' } |
    ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='Q'; $_ } |
    ForEach-Object -Begin { $state.Log+='C' } -Process { $state.Log+='R' }
$state.Log
""", [new("Output", "ABQCRP")]),
            ("source function parameter defaults bind after downstream Begin", """
$state=@{Log=''}
function Emit { param($x=$($state.Log+='D'; 1)); $state.Log+='S'; $x }
Emit | ForEach-Object -Begin { $state.Log+='B' } -Process { $state.Log+='P' }
$state.Log
""", [new("Output", "BDSP")]),
            ("append captures across stages share one collection", """
1,2 | Write-Output -OutVariable seen | Write-Output -OutVariable +seen
"seen=$($seen -join ',')"
""", [new("Output", "1"), new("Output", "2"), new("Output", "seen=1,1,2,2")]),
            ("capture initialization waits until downstream arguments are evaluated", """
$seen='initial'
$before=''
Write-Output 1 -OutVariable seen | ForEach-Object -Process $($before=$seen; { $_ })
"before=$before"
""", [new("Output", "1"), new("Output", "before=initial")]),
        };

        var failures = new List<string>();
        foreach (var (name, script, expected) in cases)
        {
            try
            {
                var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
                AssertRecords(name, result, expected);
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
            }
        }

        var commandCases = new (string Name, Func<ValueTask> Run)[]
        {
            ("legacy synchronous commands preserve source, pipeline and capture behavior", VerifyLegacyCommandAsync),
            ("asynchronous commands preserve output and awaited completion", VerifyAsyncCommandAsync),
            ("asynchronous command cancellation propagates without later output", VerifyCommandCancellationAsync),
            ("whole-invocation web commands do not repeat for each pipeline input", VerifySingleWebRequestAsync),
            ("explicit JSON input preserves once-per-invocation compatibility", VerifyExplicitJsonInputAsync),
            ("explicit Select-String input preserves once-per-invocation compatibility", VerifyExplicitSelectStringInputAsync)
        };
        foreach (var (name, run) in commandCases)
        {
            try { await run(); }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }

        var total = cases.Length + commandCases.Length;
        Console.WriteLine($"Incremental pipeline scenarios: {total - failures.Count}/{total} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static async ValueTask VerifyLegacyCommandAsync()
    {
        var calls = 0;
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Invoke-Legacy", new DelegatePowerShellWasmCommand((command, _) =>
        {
            calls++;
            var input = command.PipelineInput.Count == 0 ? command.Arguments : command.PipelineInput;
            foreach (var value in input) command.ExecutionContext.WriteOutput(value);
            command.ExecutionContext.WriteStream("Warning", "legacy-warning");
            return ValueTask.CompletedTask;
        }));
        var result = await runtime.ExecuteAsync("""
Invoke-Legacy 1 2 | ForEach-Object { "source=$_" }
$captured=@(3,4 | Invoke-Legacy)
"count=$($captured.Count)|values=$($captured -join ',')"
""").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (calls != 2) throw new InvalidOperationException($"Expected two whole-invocation callbacks, received {calls}.");
        AssertRecords("legacy command", result,
        [
            new("Output", "source=1"), new("Output", "source=2"), new("Warning", "legacy-warning"),
            new("Warning", "legacy-warning"), new("Output", "count=2|values=3,4")
        ]);
    }

    private static async ValueTask VerifyAsyncCommandAsync()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Invoke-Async", new DelegatePowerShellWasmCommand(async (command, token) =>
        {
            calls++;
            if (token != cancellation.Token) throw new InvalidOperationException("The caller cancellation token was replaced.");
            await Task.Yield();
            foreach (var value in command.PipelineInput)
            {
                await command.ExecutionContext.WriteOutputAsync(value, token);
            }
            await command.ExecutionContext.WriteStreamAsync("Warning", "async-warning", token);
        }));
        var result = await runtime.ExecuteAsync(
            "1,2 | Invoke-Async | ForEach-Object { $_ + 1 }", cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (calls != 1) throw new InvalidOperationException($"Expected one whole-input callback, received {calls}.");
        AssertRecords("async command", result,
            [new("Output", "2"), new("Output", "3"), new("Warning", "async-warning")]);
    }

    private static async ValueTask VerifyCommandCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var resumedAfterCancellation = false;
        var calls = 0;
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Cancel-Async", new DelegatePowerShellWasmCommand(async (command, token) =>
        {
            calls++;
            await Task.Yield();
            cancellation.Cancel();
            await command.ExecutionContext.WriteOutputAsync("unexpected", token);
            resumedAfterCancellation = true;
        }));
        try
        {
            await runtime.ExecuteAsync("1 | Cancel-Async | ForEach-Object { 'unreachable' }", cancellation.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Cancellation did not propagate from the command.");
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token)
        {
            if (calls != 1 || resumedAfterCancellation)
            {
                throw new InvalidOperationException("The canceled command continued executing or ran more than once.");
            }
        }
    }

    private static async ValueTask VerifySingleWebRequestAsync()
    {
        var requests = 0;
        using var handler = new FakeHttpMessageHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("response") });
        });
        var runtime = new PowerShellWasmRuntime(httpMessageHandler: handler);
        // Host commands without a streaming processor retain the existing whole-invocation adapter contract.
        var result = await runtime.ExecuteAsync(
            "1,2 | Invoke-WebRequest -Uri 'https://pipeline.example.test/' | ForEach-Object { $_.StatusCode }");
        if (requests != 1) throw new InvalidOperationException($"Expected one injected HTTP request, received {requests}.");
        AssertRecords("whole-invocation web command", result, [new("Output", "200")]);
    }

    // Existing PSWasm compatibility: explicit InputObject wins over upstream input.
    // Native PowerShell rejects this binding combination; these cases do not claim native parity.
    private static async ValueTask VerifyExplicitJsonInputAsync()
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync("""
@(1,2 | ConvertFrom-Json -InputObject '{"value":1}').Count
@(@() | ConvertFrom-Json -InputObject '{"value":1}').Count
""");
        AssertRecords("explicit JSON input", result, [new("Output", "1"), new("Output", "1")]);
    }

    private static async ValueTask VerifyExplicitSelectStringInputAsync()
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync("""
@(1,2 | Select-String -InputObject 'abc' -Pattern 'a').Count
@(@() | Select-String -InputObject 'abc' -Pattern 'a').Count
""");
        AssertRecords("explicit Select-String input", result, [new("Output", "1"), new("Output", "1")]);
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
