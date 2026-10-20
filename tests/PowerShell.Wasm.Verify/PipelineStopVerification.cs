using PSWasm;

internal static class PipelineStopVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected script records were verified against PowerShell 7.6.1.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("First1", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 1); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=P1")]),
            ("First0", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 0); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P1A1P2A2P3A3P4A4E")]),
            ("First1Wait", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 1 -Wait); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=P1A1P2A2P3A3P4A4E")]),
            ("First0Wait", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 0 -Wait); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P1A1P2A2P3A3P4A4E")]),
            ("Skip2First1", """
$s=@{L=""}; $r=@(1..5 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -Skip 2 -First 1); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=3;l=P1A1P2A2P3")]),
            ("First1Last2", """
$s=@{L=""}; $r=@(1..5 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 1 -Last 2); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1,4,5;l=P1A1P2A2P3A3P4A4P5A5E")]),
            ("First3Last3", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 3 -Last 3); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1,2,3,4;l=P1A1P2A2P3A3P4A4E")]),
            ("First1Last0", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 1 -Last 0); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=P1")]),
            ("First0Last2", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object {$s.L+="P$_"; $_; $s.L+="A$_"} -End {$s.L+="E"} | Select-Object -First 0 -Last 2); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=3,4;l=P1A1P2A2P3A3P4A4E")]),
            ("EmptyFirst1", """
$s=@{L=""}; $r=@(&{} | ForEach-Object -Begin {$s.L+="B"} -Process {$s.L+="P";$_} -End {$s.L+="E"} | Select-Object -First 1 | ForEach-Object -Begin {$s.L+="b"} -Process {$s.L+="p";$_} -End {$s.L+="e"}); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=BbEe")]),
            ("DownstreamEnd", """
$s=@{L=""}; $r=@(1..4 | ForEach-Object -Begin {$s.L+="B"} -Process {$s.L+="P";$_;$s.L+="A"} -End {$s.L+="E"} | Select-Object -First 1 | ForEach-Object -Begin {$s.L+="b"} -Process {$s.L+="p";$_} -End {$s.L+="e";99}); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1,99;l=BbPpe")]),
            ("SourceFinally", """
$s=@{L=""}; function Source {try {foreach($n in 1..4){$s.L+="P$n";$n;$s.L+="A$n"}}catch{$s.L+="C"}finally{$s.L+="F";99};$s.L+="Z"}; $r=@(Source | Select-Object -First 1); "r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=P1F")]),
            ("InnerStopContinues", """
$s=@{L=""}; function Source {$s.L+="B";1..4 | ForEach-Object {$s.L+="P$_";$_} | Select-Object -First 1;$s.L+="A";99};$r=@(Source | ForEach-Object {$_});"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1,99;l=BP1A")]),
            ("OuterStopNested", """
$s=@{L=""}; function Source {try {1..4 | ForEach-Object {$s.L+="P$_";$_;$s.L+="A$_"} | ForEach-Object {$_}; $s.L+="X"}finally{$s.L+="F"};$s.L+="Z"};$r=@(Source | Select-Object -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=P1F")]),
            ("NullFirst1", """
$s=@{L=""};$r=@($null,1,2 | ForEach-Object {$s.L+="P";$_;$s.L+="A"} -End {$s.L+="E"} | Select-Object -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=PAP")]),
            ("ErrorAfterStop", """
$s=@{L=""};$r=@(1..4 | ForEach-Object {$_;throw "after"} | Select-Object -First 1);$ok=$?;"r=$($r -join ',');ok=$ok"
""", [new("Output", "r=1;ok=True")]),
            ("Skip2Last2", """
$s=@{L=""};$r=@(1..7 | ForEach-Object {$s.L+="P$_";$_} | Select-Object -Skip 2 -Last 2);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=4,5;l=P1P2P3P4P5P6P7")]),
            ("Skip2First1Last2", """
$s=@{L=""};$r=@(1..7 | ForEach-Object {$s.L+="P$_";$_} | Select-Object -Skip 2 -First 1 -Last 2);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=3,6,7;l=P1P2P3P4P5P6P7")]),
            ("Skip2First0Last2", """
$s=@{L=""};$r=@(1..7 | ForEach-Object {$s.L+="P$_";$_} | Select-Object -Skip 2 -First 0 -Last 2);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=4,5;l=P1P2P3P4P5P6P7")]),
            ("Last0", """
$s=@{L=""};$r=@(1..4 | ForEach-Object {$s.L+="P$_";$_} -End {$s.L+="E"} | Select-Object -Last 0);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P1P2P3P4E")]),
            ("First0Last0", """
$s=@{L=""};$r=@(1..4 | ForEach-Object {$s.L+="P$_";$_} -End {$s.L+="E"} | Select-Object -First 0 -Last 0);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P1P2P3P4E")]),
            ("BeginOutput", """
$s=@{L=""};$r=@(1..4 | ForEach-Object -Begin {$s.L+="B";98;$s.L+="b"} -Process {$s.L+="P";$_} -End {$s.L+="E"} | Select-Object -First 1 | ForEach-Object -Begin {$s.L+="D"} -Process {$_} -End {$s.L+="d";99});"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=98,99;l=BDd")]),
            ("ExpandEmptyFirst", """
$s=@{L=""};$r=@(@{V=@()},@{V=@(10,11)},@{V=@(20)} | ForEach-Object {$s.L+="P";$_;$s.L+="A"} | Select-Object -ExpandProperty V -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P")]),
            ("ExpandNullFirst", """
$s=@{L=""};$r=@(@{V=$null},@{V=@(10,11)} | ForEach-Object {$s.L+="P";$_;$s.L+="A"} | Select-Object -ExpandProperty V -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=;l=P")]),
            ("Wait false permits immediate stopping", """
$s=@{N=0};$r=@(1..5 | ForEach-Object {$s.N++;$_} | Select-Object -First 2 -Wait:$false);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1,2|n=2")]),
            ("Wait true consumes source after selecting the first item", """
$s=@{N=0};$r=@(1..5 | ForEach-Object {$s.N++;$_} | Select-Object -First 2 -Wait:$true);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1,2|n=5")]),
            ("splatting binds Wait false and selection bounds", """
$s=@{N=0};$options=@{First=2;Skip=1;Wait=$false};$r=@(1..5 | ForEach-Object {$s.N++;$_} | Select-Object @options);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=2,3|n=3")]),
            ("splatting binds Wait true", """
$s=@{N=0};$options=@{First=1;Wait=$true};$r=@(1..4 | ForEach-Object {$s.N++;$_} | Select-Object @options);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1|n=4")]),
            ("downstream smaller selector owns the pipeline stop", """
$s=@{N=0;End=0};$r=@(1..5 | ForEach-Object {$s.N++;$_} -End {$s.End++} | Select-Object -First 3 | Select-Object -First 1);"r=$($r -join ',')|n=$($s.N)|end=$($s.End)"
""", [new("Output", "r=1|n=1|end=0")]),
            ("upstream smaller selector still completes downstream stages", """
$s=@{N=0;End=0};$r=@(1..5 | ForEach-Object {$s.N++;$_} | Select-Object -First 1 | Select-Object -First 3 | ForEach-Object {$_} -End {$s.End++});"r=$($r -join ',')|n=$($s.N)|end=$($s.End)"
""", [new("Output", "r=1|n=1|end=1")]),
            ("buffered downstream receives final completion after a stop", """
$s=@{N=0};$r=@(3,1,2 | ForEach-Object {$s.N++;$_} | Select-Object -First 2 | Sort-Object);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1,3|n=2")]),
            ("buffered upstream finishes before downstream selection", """
$s=@{N=0;End=0};$r=@(3,1,2 | ForEach-Object {$s.N++;$_} -End {$s.End++} | Sort-Object | Select-Object -First 1);"r=$($r -join ',')|n=$($s.N)|end=$($s.End)"
""", [new("Output", "r=1|n=3|end=1")]),
            ("stop is successful and does not create error records", """
$s=@{N=0};$before=$Error.Count;$r=@(1..3 | ForEach-Object {$s.N++;$_} -ErrorVariable errors | Select-Object -First 1);$ok=$?;"r=$($r -join ',')|ok=$ok|newerrors=$($Error.Count - $before)|ev=$($errors.Count)"
""", [new("Output", "r=1|ok=True|newerrors=0|ev=0")]),
            ("captures contain only emitted records and pipeline variables clear", """
$r=@(1..4 | Write-Output -OutVariable source -PipelineVariable current | Select-Object -First 2 -OutVariable selected -PipelineVariable chosen | ForEach-Object {"item=$_|pv=$current|selected=$chosen|seen=$($source.Count)"});$r;"source=$($source -join ',')|selected=$($selected -join ',')|pvnull=$($null -eq $current)|chosenNull=$($null -eq $chosen)"
""", [new("Output", "item=1|pv=1|selected=1|seen=1"), new("Output", "item=2|pv=2|selected=2|seen=2"), new("Output", "source=1,2|selected=1,2|pvnull=True|chosenNull=True")]),
            ("append capture retains earlier values after selection stops", """
$seen=@('old');1..4 | Write-Output -OutVariable +seen | Select-Object -First 1;"seen=$($seen -join ',')"
""", [new("Output", "1"), new("Output", "seen=old,1")]),
            ("source catch does not receive the internal stop", """
$s=@{Log=''};function Emit {try {1;$s.Log+='after';throw 'unexpected'}catch{$s.Log+='catch'}finally{$s.Log+='finally'}};$r=@(Emit | Select-Object -First 1);"r=$($r -join ',')|log=$($s.Log)"
""", [new("Output", "r=1|log=finally")]),
            ("count beyond input runs all completion blocks", """
$s=@{N=0;End=0};$r=@(1..3 | ForEach-Object {$s.N++;$_} -End {$s.End++} | Select-Object -First 9);"r=$($r -join ',')|n=$($s.N)|end=$($s.End)"
""", [new("Output", "r=1,2,3|n=3|end=1")]),
            ("all-null input never reaches the first selection limit", """
$s=@{N=0;End=0};$r=@($null,$null | ForEach-Object {$s.N++;$_} -End {$s.End++} | Select-Object -First 1);"count=$($r.Count)|n=$($s.N)|end=$($s.End)"
""", [new("Output", "count=0|n=2|end=1")]),
            ("nested stopped pipelines preserve outer loop progress", """
$s=@{Log=''};1,2 | ForEach-Object {$outer=$_;10,20 | ForEach-Object {$s.Log+="I$outer/$_;";$_} | Select-Object -First 1;"outer=$_"};$s.Log
""", [new("Output", "10"), new("Output", "outer=1"), new("Output", "10"), new("Output", "outer=2"), new("Output", "I1/10;I2/10;")]),
            ("empty source selection preserves downstream End output", """
$r=@(@() | Select-Object -First 1 | ForEach-Object {'unexpected'} -End {'end'});"r=$($r -join ',')"
""", [new("Output", "r=end")]),
            ("Wait allows a real later terminating error", """
$s=@{Log=''};try {1..3 | ForEach-Object {$_;$s.Log+='A';throw 'later'} | Select-Object -First 1 -Wait}catch{$s.Log+='C'};$s.Log
""", [new("Output", "1"), new("Output", "AC")]),
            ("short-circuit pipeline chains retain successful status", """
1..4 | Select-Object -First 1 && 'success'; 1..4 | Select-Object -First 1 || 'unexpected'
""", [new("Output", "1"), new("Output", "success"), new("Output", "1")]),
            ("finally success output retriggers stop before later side effects", """
$s=@{Log=''};function Emit {try {1;2}finally{$s.Log+='F';99;$s.Log+='G'}};$r=@(Emit | Select-Object -First 1);"r=$($r -join ',')|log=$($s.Log)"
""", [new("Output", "r=1|log=F")]),
            ("finally side streams complete while success input is stopped", """
$s=@{Log=''};function Emit {try {1;2}finally{$s.Log+='F';Write-Warning 'cleanup-warning';Write-Information 'cleanup-info' -InformationAction Continue;Write-Host 'cleanup-host';$s.Log+='G'}};$r=@(Emit | Select-Object -First 1);"r=$($r -join ',')|log=$($s.Log)"
""", [new("Warning", "cleanup-warning"), new("Information", "cleanup-info"), new("Information", "cleanup-host"), new("Output", "r=1|log=FG")]),
            ("pending stop wins over finally throw without reaching user catch", """
$s=@{Log=''};$before=$Error.Count;function Emit {try {1;2}finally{$s.Log+='F';throw 'cleanup-throw'}};try {$r=@(Emit | Select-Object -First 1);$ok=$?}catch{$s.Log+='C'};"r=$($r -join ',')|log=$($s.Log)|ok=$ok|newerrors=$($Error.Count-$before)"
""", [new("Output", "r=1|log=F|ok=True|newerrors=1")]),
            ("source End output can stop before upstream End resumes", """
$s=@{Log=''};$r=@(@() | ForEach-Object -Process {$_} -End {$s.Log+='E';1;$s.Log+='A';2} | Select-Object -First 1 | ForEach-Object {$_} -End {$s.Log+='D'});"r=$($r -join ',')|log=$($s.Log)"
""", [new("Output", "r=1|log=ED")]),
            ("source function with a finite source limit stops at three", """
$s=@{N=0};function Emit {while ($s.N -lt 100) {$s.N++;$s.N}};$r=@(Emit | Select-Object -First 3);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1,2,3|n=3")]),
            ("finally throw under pending stop allows success chain", """
$before=$Error.Count;function Emit {try {1;2}finally{throw 'cleanup'}};Emit | Select-Object -First 1 && 'success';"newerrors=$($Error.Count-$before)"
""", [new("Output", "1"), new("Output", "success"), new("Output", "newerrors=1")]),
            ("pending stop ignores finally WarningAction Stop for pipeline status", """
$s=@{Log=''};$before=$Error.Count;function Emit {try {1;2}finally{Write-Warning 'cleanup' -WarningAction Stop;$s.Log+='after'}};try{$r=@(Emit | Select-Object -First 1);$ok=$?}catch{$s.Log+='caught'};"r=$($r -join ',')|ok=$ok|newerrors=$($Error.Count-$before)|log=$($s.Log)"
""", [new("Warning", "cleanup"), new("Output", "r=1|ok=True|newerrors=1|log=")]),
            ("expanded collection counts as one selected input object", """
$s=@{N=0};$r=@(@{V=@(10,11)},@{V=@(20,21)} | ForEach-Object {$s.N++;$_} | Select-Object -First 1 -ExpandProperty V);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=10,11|n=1")]),
            ("same source function remains usable after a stopped invocation", """
$s=@{N=0};function Emit {foreach($n in 1..4){$s.N++;$n}};Emit | Select-Object -First 1;"n=$($s.N)";Emit | Select-Object -First 2;"n=$($s.N)"
""", [new("Output", "1"), new("Output", "n=1"), new("Output", "1"), new("Output", "2"), new("Output", "n=3")]),
            ("upstream direct Write-Error status with selected empty input", """
Write-Error 'before' | Select-Object -First 1 && 'success' || 'failure'
""", [new("Error", "before"), new("Output", "failure")]),
            ("Skip alone drains the source and returns remaining input", """
$s=@{N=0};$r=@(1..5 | ForEach-Object {$s.N++;$_} | Select-Object -Skip 2);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=3,4,5|n=5")]),
            ("default selection passes every non-null input", """
$s=@{N=0};$r=@(1,$null,2,3 | ForEach-Object {$s.N++;$_} | Select-Object);"r=$($r -join ',')|n=$($s.N)"
""", [new("Output", "r=1,2,3|n=4")]),
            ("FinallyNestedCaptured", """
$s=@{L=""};function Source{try{1;2}finally{$s.L+="F";$a=@(99;100);$s.L+="G:$($a.Count)"}};$r=@(Source | Select-Object -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=FG:2")]),
            ("SourceEndStopped", """
$s=@{L=""};$r=@(&{} | ForEach-Object -Process {} -End {$s.L+="E";98;$s.L+="e";99} | Select-Object -First 1 | ForEach-Object -Process {$_} -End {$s.L+="D";100});"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=98,100;l=ED")]),
            ("FinallyLocalPipeline", """
$s=@{L=""};function Source{try{1;2}finally{$s.L+="F";$a=@(1..3 | ForEach-Object {$s.L+="P";$_} | Select-Object -First 1);$s.L+="G:$($a.Count)"}};$r=@(Source | Select-Object -First 1);"r=$($r -join ',');l=$($s.L)"
""", [new("Output", "r=1;l=FPG:1")]),
            ("upstream captures observe attempted cleanup output before the stop repeats", """
$s=@{Log=''};$r=@(1..3 | ForEach-Object {try{$_}finally{$s.Log+='F';99;$s.Log+='G'}} -OutVariable source -PipelineVariable current | Select-Object -First 1 -OutVariable selected);"r=$($r -join ',')|source=$($source -join ',')|selected=$($selected -join ',')|pvnull=$($null -eq $current)|log=$($s.Log)"
""", [new("Output", "r=1|source=1,99|selected=1|pvnull=True|log=F")]),
            ("downstream End terminating error propagates after an upstream stop", """
$s=@{N=0}
$before=$Error.Count
function Emit { foreach($n in 1..4) { $s.N++; $n } }
try {
    Emit | Select-Object -First 1 | ForEach-Object -Process { $_ } -End { Write-Warning 'end-warning'; throw 'end-failure' }
} catch { "caught=$($_.Exception.Message)" }
"n=$($s.N)|newerrors=$($Error.Count-$before)"
""", [new("Output", "1"), new("Warning", "end-warning"), new("Output", "caught=end-failure"), new("Output", "n=1|newerrors=1")]),
            ("downstream End error supersedes suppressed producer cleanup error", """
$before=$Error.Count
function Emit { try { 1; 2 } finally { throw 'cleanup-failure' } }
try {
    Emit | Select-Object -First 1 | ForEach-Object -Process { $_ } -End { throw 'end-failure' }
} catch { "caught=$($_.Exception.Message)" }
"newerrors=$($Error.Count-$before)"
""", [new("Output", "1"), new("Output", "caught=end-failure"), new("Output", "newerrors=2")]),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected) in cases)
        {
            try { AssertRecords(name, await new PowerShellWasmRuntime().ExecuteAsync(script), expected); }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }

        var hostCases = new (string Name, Func<ValueTask> Run)[]
        {
            ("an unbounded source stops without caller cancellation", VerifyUnboundedSourceAsync),
            ("stopped invocation leaves the runtime reusable", VerifyRuntimeReuseAsync),
            ("caller cancellation is not swallowed by a pending selection stop", VerifyCancellationDuringFinallyAsync)
        };
        foreach (var (name, run) in hostCases)
        {
            try { await run(); }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        var total = cases.Length + hostCases.Length;
        Console.WriteLine($"Pipeline stop scenarios: {total - failures.Count}/{total} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static async ValueTask VerifyUnboundedSourceAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new PowerShellWasmRuntime();
        var result = await runtime.ExecuteAsync("""
$state=@{N=0}
function Emit { while ($true) { $state.N++; $state.N } }
Emit | Select-Object -First 3
"produced=$($state.N)"
""", cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Selection depended on caller cancellation to stop its source.");
        }
        AssertRecords("unbounded source", result,
            [new("Output", "1"), new("Output", "2"), new("Output", "3"), new("Output", "produced=3")]);
    }

    private static async ValueTask VerifyRuntimeReuseAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        var first = await runtime.ExecuteAsync("""
$scope='caller'
$state=@{N=0}
function Emit { param($scope='source'); foreach ($n in 1..4) { $state.N++; $n } }
Emit | Select-Object -First 1 -PipelineVariable selected
"produced=$($state.N)"
""");
        AssertRecords("first invocation", first, [new("Output", "1"), new("Output", "produced=1")]);
        var second = await runtime.ExecuteAsync("""
"scope=$scope|selectedNull=$($null -eq $selected)"
Emit | Select-Object -First 2
"produced=$($state.N)"
1,2 | ForEach-Object { $_ + 10 }
""");
        AssertRecords("next invocation", second,
        [
            new("Output", "scope=caller|selectedNull=True"), new("Output", "1"), new("Output", "2"),
            new("Output", "produced=3"), new("Output", "11"), new("Output", "12")
        ]);
    }

    private static async ValueTask VerifyCancellationDuringFinallyAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Cancel-Now", new DelegatePowerShellWasmCommand((_, token) =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }));
        try
        {
            await runtime.ExecuteAsync("""
function Emit { try { 1; 2 } finally { Cancel-Now } }
Emit | Select-Object -First 1
'unreachable'
""", cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Caller cancellation was swallowed during source cleanup.");
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token)
        {
            if (calls != 1) throw new InvalidOperationException($"Expected one cleanup callback, received {calls}.");
        }
        AssertRecords("reuse after cancellation", await runtime.ExecuteAsync("1,2 | ForEach-Object { $_ + 10 }"),
            [new("Output", "11"), new("Output", "12")]);
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

