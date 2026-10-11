using PSWasm;

internal static class OutputCaptureVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell verifies record boundaries, stream order, and script-observable value shapes.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("expression statements enumerate output arrays and grouped chains", """
$a=@(1,2); $a
(3,4)
(Write-Output 5 && Write-Output 6)
""", [new("Output", "1"), new("Output", "2"), new("Output", "3"), new("Output", "4"), new("Output", "5"), new("Output", "6")]),

            ("nested array expression pipeline enumerates only one level", """
$nested=@(@(1,2),@(3,4))
$nested | ForEach-Object { "array=$($_ -is [array])|count=$($_.Count)|values=$($_ -join ',')" }
""", [new("Output", "array=True|count=2|values=1,2"), new("Output", "array=True|count=2|values=3,4")]),

            ("function return enumerates ordinary arrays and preserves comma protected arrays", """
function Many { return 1,2; 'unreachable' }
Many
function Protected { return ,@(3,4) }
$a=@(Protected)
"outer=$($a.Count)|inner=$($a[0].Count)|values=$($a[0] -join ',')"
""", [new("Output", "1"), new("Output", "2"), new("Output", "outer=1|inner=2|values=3,4")]),

            ("top level return enumerates values and ends execution", """
return 1,2
'unreachable'
""", [new("Output", "1"), new("Output", "2")]),

            ("array subexpression captures zero one and multiple success values", """
$a=@(); "empty=$($a.Count)"
$a=@(7); "single=$($a.Count)|array=$($a -is [array])|value=$($a[0])"
$a=@(1,2;3); "many=$($a.Count)|values=$($a -join ',')"
$a=@(,@(1,2)); "protected=$($a.Count)|inner=$($a[0].Count)|values=$($a[0] -join ',')"
""", [new("Output", "empty=0"), new("Output", "single=1|array=True|value=7"), new("Output", "many=3|values=1,2,3"), new("Output", "protected=1|inner=2|values=1,2")]),

            ("subexpression and parentheses collapse success output consistently", """
$a=$(7); "single=$($a -is [int])|value=$a"
$a=$(1;2); "many=$($a -is [array])|count=$($a.Count)|values=$($a -join ',')"
$a=(Write-Output 3,4); "paren=$($a -is [array])|count=$($a.Count)|values=$($a -join ',')"
$a=$(,@(5,6)); "protected=$($a.Count)|values=$($a -join ',')"
$a=@($(,@(5,6))); "reenumerated=$($a.Count)|values=$($a -join ',')"
""", [new("Output", "single=True|value=7"), new("Output", "many=True|count=2|values=1,2"), new("Output", "paren=True|count=2|values=3,4"), new("Output", "protected=2|values=5,6"), new("Output", "reenumerated=2|values=5,6")]),

            ("explicit null emits one success record while no output emits none", """
function Empty {}
$null
Empty
Write-Output $null
@()
'end'
""", [new("Output", ""), new("Output", ""), new("Output", "end")]),

            ("null and no output have equivalent scalar semantics but different capture counts", """
function Empty {}
$none=Empty; $nil=$null
"none:null=$($none -eq $null)|bool=$([bool]$none)|str=<$([string]$none)>|int=$([int]$none)|fallback=$($none ?? 'fallback')|count=$($none.Count)"
"nil:null=$($nil -eq $null)|bool=$([bool]$nil)|str=<$([string]$nil)>|int=$([int]$nil)|fallback=$($nil ?? 'fallback')|count=$($nil.Count)"
"capture:none=$(@($none).Count)|nil=$(@($nil).Count)"
$x=$none; "copied=$(@($x).Count)|undefined=$(@($undefined).Count)"
""", [new("Output", "none:null=True|bool=False|str=<>|int=0|fallback=fallback|count=0"), new("Output", "nil:null=True|bool=False|str=<>|int=0|fallback=fallback|count=0"), new("Output", "capture:none=0|nil=1"), new("Output", "copied=0|undefined=1")]),

            ("comma arrays retain no output positions before pipeline enumeration", """
function Empty {}
$none=Empty
$a=,$none; "count=$($a.Count)|firstNull=$($null -eq $a[0])|capture=$(@($a).Count)"
$a=@(1,$none,2); "count=$($a.Count)|values=$($a -join ',')|capture=$(@($a).Count)"
$a=@($none;1); "statementCount=$($a.Count)|values=$($a -join ',')"
$a=@($null;1); "nullCount=$($a.Count)|values=$($a -join ',')"
$a=$none,1; "direct=$(@($a).Count)|pipeline=$(@($a | ForEach-Object { 'item' }).Count)|firstCapture=$(@($a[0]).Count)"
$a=,$none; "commaDirect=$(@($a).Count)|commaPipeline=$(@($a | ForEach-Object { 'item' }).Count)|firstCapture=$(@($a[0]).Count)"
""", [new("Output", "count=1|firstNull=True|capture=1"), new("Output", "count=3|values=1,,2|capture=3"), new("Output", "statementCount=1|values=1"), new("Output", "nullCount=2|values=,1"), new("Output", "direct=2|pipeline=1|firstCapture=0"), new("Output", "commaDirect=1|commaPipeline=0|firstCapture=0")]),

            ("null and no output differ as pipeline input", """
function Empty {}
$none=Empty
$r=@($none | ForEach-Object { 'unexpected' }); "none=$($r.Count)"
$r=@($null | ForEach-Object { "null=$($null -eq $_)" }); "nullCount=$($r.Count)|value=$($r[0])"
$r=@(@() | ForEach-Object { 'unexpected' }); "empty=$($r.Count)"
$r=@(,@() | ForEach-Object { "innerCount=$($_.Count)" }); "protected=$($r.Count)|value=$($r[0])"
""", [new("Output", "none=0"), new("Output", "nullCount=1|value=null=True"), new("Output", "empty=0"), new("Output", "protected=1|value=innerCount=0")]),

            ("return no expression differs from explicit null return", """
function Nothing { return }
function NullValue { return $null }
$a=@(Nothing); "none=$($a.Count)"
$a=@(NullValue); "null=$($a.Count)|isNull=$($null -eq $a[0])"
$a=@($(Nothing)); "nestedNone=$($a.Count)"
$a=@($(NullValue)); "nestedNull=$($a.Count)"
""", [new("Output", "none=0"), new("Output", "null=1|isNull=True"), new("Output", "nestedNone=0"), new("Output", "nestedNull=1")]),

            ("byte arrays enumerate scalar bytes in expressions and pipelines", """
$bytes=[byte[]]@(1,2)
$bytes
$bytes | ForEach-Object { "$_|byte=$($_ -is [byte])|array=$($_ -is [array])" }
$a=@($bytes); "count=$($a.Count)|firstByte=$($a[0] -is [byte])"
function Bytes { return [byte[]]@(3,4) }
$a=@(Bytes); "returnCount=$($a.Count)|firstByte=$($a[0] -is [byte])|values=$($a -join ',')"
""", [new("Output", "1"), new("Output", "2"), new("Output", "1|byte=True|array=False"), new("Output", "2|byte=True|array=False"), new("Output", "count=2|firstByte=True"), new("Output", "returnCount=2|firstByte=True|values=3,4")]),

            ("comma protects byte array identity for pipeline consumers", """
$bytes=[byte[]]@(1,2)
,$bytes | ForEach-Object { "byteArray=$($_ -is [byte[]])|count=$($_.Count)|values=$($_ -join ',')" }
$a=@(,$bytes); "outer=$($a.Count)|innerByteArray=$($a[0] -is [byte[]])|innerCount=$($a[0].Count)"
""", [new("Output", "byteArray=True|count=2|values=1,2"), new("Output", "outer=1|innerByteArray=True|innerCount=2")]),

            ("write output default enumerates ordinary arrays once", """
Write-Output @(1,2)
Write-Output @(@(3,4),@(5,6)) | ForEach-Object { "array=$($_ -is [array])|count=$($_.Count)|values=$($_ -join ',')" }
$a=@(Write-Output @()); "empty=$($a.Count)"
""", [new("Output", "1"), new("Output", "2"), new("Output", "array=True|count=2|values=3,4"), new("Output", "array=True|count=2|values=5,6"), new("Output", "empty=0")]),

            ("write output no enumerate preserves arrays and empty arrays", """
$a=@(Write-Output -NoEnumerate @(1,2))
"outer=$($a.Count)|array=$($a[0] -is [array])|inner=$($a[0].Count)|values=$($a[0] -join ',')"
$a=@(Write-Output -NoEnumerate @())
"emptyOuter=$($a.Count)|inner=$($a[0].Count)"
Write-Output -NoEnumerate @(3,4) | ForEach-Object { "pipelineCount=$($_.Count)|values=$($_ -join ',')" }
""", [new("Output", "outer=1|array=True|inner=2|values=1,2"), new("Output", "emptyOuter=1|inner=0"), new("Output", "pipelineCount=2|values=3,4")]),

            ("write output no enumerate wraps scalar and null arguments", """
$x=Write-Output -NoEnumerate 1
"int=$($x -is [int])|array=$($x -is [array])|count=$($x.Count)|value=$($x[0])"
$x=Write-Output -NoEnumerate $null
"isNull=$($null -eq $x)|count=$($x.Count)|firstNull=$($null -eq $x[0])"
$x=Write-Output -NoEnumerate 'text'
"string=$($x -is [string])|count=$($x.Count)|value=$($x[0])"
""", [new("Output", "int=False|array=False|count=1|value=1"), new("Output", "isNull=False|count=1|firstNull=True"), new("Output", "string=False|count=1|value=text")]),

            ("write output no enumerate processes each pipeline input independently", """
$a=@(1,2 | Write-Output -NoEnumerate)
"count=$($a.Count)|firstCount=$($a[0].Count)|secondCount=$($a[1].Count)|values=$($a -join ',')"
$a=@(,@(3,4) | Write-Output -NoEnumerate)
"protectedCount=$($a.Count)|innerCount=$($a[0].Count)|values=$($a[0] -join ',')"
$a=@($null | Write-Output -NoEnumerate)
"nullCount=$($a.Count)|firstNull=$($null -eq $a[0])"
""", [new("Output", "count=2|firstCount=1|secondCount=1|values=1,2"), new("Output", "protectedCount=1|innerCount=2|values=3,4"), new("Output", "nullCount=1|firstNull=True")]),

            ("parentheses reenumerate no enumerate command output", """
$a=@((Write-Output -NoEnumerate @(1,2)))
"count=$($a.Count)|values=$($a -join ',')"
$a=@(Write-Output -NoEnumerate @(1,2))
"directCount=$($a.Count)|innerCount=$($a[0].Count)"
$a=@($(Write-Output -NoEnumerate @(1,2)))
"subexpressionCount=$($a.Count)|values=$($a -join ',')"
""", [new("Output", "count=2|values=1,2"), new("Output", "directCount=1|innerCount=2"), new("Output", "subexpressionCount=2|values=1,2")]),

            ("hashtables and strings remain scalar pipeline values", """
@{A=1;B=2} | ForEach-Object { "map=$($_ -is [hashtable])|count=$($_.Count)|a=$($_.A)|b=$($_.B)" }
'abc' | ForEach-Object { "string=$($_ -is [string])|length=$($_.Length)|value=$_" }
""", [new("Output", "map=True|count=2|a=1|b=2"), new("Output", "string=True|length=3|value=abc")]),

            ("nested assignment capture forwards all visible side streams in order", """
$VerbosePreference='Continue'; $DebugPreference='Continue'; $InformationPreference='Continue'
$x=$(Write-Warning 'warn'; Write-Verbose 'verbose'; Write-Debug 'debug'; Write-Information 'info'; Write-Error 'err'; 1,2)
"count=$($x.Count)|values=$($x -join ',')"
""", [new("Warning", "warn"), new("Verbose", "verbose"), new("Debug", "debug"), new("Information", "info"), new("Error", "err"), new("Output", "count=2|values=1,2")]),

            ("nested array and subexpression captures forward warnings once", """
$x=@($(Write-Warning 'outer'; $(Write-Warning 'inner'; 1,2)))
"count=$($x.Count)|values=$($x -join ',')"
""", [new("Warning", "outer"), new("Warning", "inner"), new("Output", "count=2|values=1,2")]),

            ("function and pipeline captures preserve side streams and success values", """
$VerbosePreference='Continue'
function Emit { Write-Warning 'source-warning'; 1,2 }
$x=@(Emit | ForEach-Object { Write-Verbose "seen=$_"; "value=$_" })
"count=$($x.Count)|values=$($x -join ',')"
""", [new("Warning", "source-warning"), new("Verbose", "seen=1"), new("Verbose", "seen=2"), new("Output", "count=2|values=value=1,value=2")]),

            ("side streams interleave with downstream processing in source order", """
$VerbosePreference='Continue'
function Emit { 1; Write-Warning 'between'; 2 }
$x=@(Emit | ForEach-Object { Write-Verbose "seen=$_"; "value=$_" })
"count=$($x.Count)|values=$($x -join ',')"
""", [new("Verbose", "seen=1"), new("Warning", "between"), new("Verbose", "seen=2"), new("Output", "count=2|values=value=1,value=2")]),

            ("common parameter output capture respects protected collection records", """
$x=@(Write-Output -NoEnumerate @(1,2) -OutVariable seen)
"result=$($x.Count)|resultInner=$($x[0].Count)|seen=$($seen.Count)|seenInner=$($seen[0].Count)"
$x=@(Write-Output 3,4 -OutVariable seen | ForEach-Object { "value=$_" })
"result=$($x.Count)|values=$($x -join ',')|seen=$($seen.Count)|seenValues=$($seen -join ',')"
""", [new("Output", "result=1|resultInner=2|seen=1|seenInner=2"), new("Output", "result=2|values=value=3,value=4|seen=2|seenValues=3,4")]),

            ("warning common parameter capture remains visible through nested success capture", """
$x=$(Write-Warning 'captured-warning' -WarningVariable warnings; 7)
"result=$x|warningCount=$($warnings.Count)|warning=$($warnings[0])"
""", [new("Warning", "captured-warning"), new("Output", "result=7|warningCount=1|warning=captured-warning")]),

            ("assignment and increment statement capture distinguish no output from values", """
$n=1
$a=@($n=2); "assignment=$($a.Count)|n=$n"
$a=@(($n=3)); "grouped=$($a.Count)|value=$($a[0])"
$a=$(++$n); "incrementNoOutput=$(@($a).Count)|n=$n"
$a=@(++$n); "incrementArray=$($a.Count)|value=$($a[0])|n=$n"
""", [new("Output", "assignment=0|n=2"), new("Output", "grouped=1|value=3"), new("Output", "incrementNoOutput=0|n=4"), new("Output", "incrementArray=1|value=5|n=5")]),

            ("named no enumerate preserves actual scalar null and byte array inputs", """
$x=Write-Output -NoEnumerate -InputObject 1
"int=$($x -is [int])|count=$($x.Count)|value=$x"
$x=Write-Output -NoEnumerate -InputObject $null
"null=$($null -eq $x)|captured=$(@($x).Count)"
$bytes=[byte[]]@(3,4)
$x=@(Write-Output -NoEnumerate -InputObject $bytes)
"outer=$($x.Count)|bytes=$($x[0] -is [byte[]])|inner=$($x[0].Count)"
$x=@(Write-Output -NoEnumerate:$false -InputObject @(5,6))
"enumerated=$($x.Count)|values=$($x -join ',')"
""", [new("Output", "int=True|count=1|value=1"), new("Output", "null=True|captured=1"), new("Output", "outer=1|bytes=True|inner=2"), new("Output", "enumerated=2|values=5,6")]),

            ("function parameter binding converts no output arguments to explicit null", """
function Empty {}
$none=Empty
function Inspect {
    param($Value='fallback')
    "null=$($null -eq $Value)|count=$(@($Value).Count)|bound=$($PSBoundParameters.ContainsKey('Value'))|value=<$Value>"
}
Inspect
Inspect $none
Inspect -Value $none
$arguments=,$none; Inspect @arguments
$named=@{Value=$none}; Inspect @named
""", [new("Output", "null=False|count=1|bound=False|value=<fallback>"), new("Output", "null=True|count=1|bound=True|value=<>"), new("Output", "null=True|count=1|bound=True|value=<>"), new("Output", "null=True|count=1|bound=True|value=<>"), new("Output", "null=True|count=1|bound=True|value=<>")]),

            ("typed function parameters convert no output with native scalar rules", """
function Empty {}
$none=Empty
function InspectTyped {
    param([string]$Text, [int]$Number, [object]$Value)
    "text=<$Text>|number=$Number|numberType=$($Number -is [int])|null=$($null -eq $Value)|capture=$(@($Value).Count)"
}
InspectTyped $none $none $none
InspectTyped -Text $none -Number $none -Value $none
""", [new("Output", "text=<>|number=0|numberType=True|null=True|capture=1"), new("Output", "text=<>|number=0|numberType=True|null=True|capture=1")]),

            ("write output binds no output arguments as null instead of omitting them", """
function Empty {}
$none=Empty
$a=@(Write-Output $none); "positional=$($a.Count)|firstNull=$($null -eq $a[0])"
$a=@(Write-Output -InputObject $none); "named=$($a.Count)|firstNull=$($null -eq $a[0])"
$a=@(Write-Output -NoEnumerate -InputObject $none); "protectedNamed=$($a.Count)|firstNull=$($null -eq $a[0])"
$a=@(Write-Output -NoEnumerate $none); "protectedPositional=$($a.Count)|inner=$($a[0].Count)|firstNull=$($null -eq $a[0][0])"
""", [new("Output", "positional=1|firstNull=True"), new("Output", "named=1|firstNull=True"), new("Output", "protectedNamed=1|firstNull=True"), new("Output", "protectedPositional=1|inner=1|firstNull=True")]),

            ("array expression optimization preserves markers but statement capture omits them", """
function Empty {}
$none=Empty; $values=$none,1
$a=@($values); "expression=$($a.Count)|values=$($a -join ',')"
$a=@($values;); "trailingSemicolon=$($a.Count)|values=$($a -join ',')"
$a=@($values;2); "statements=$($a.Count)|values=$($a -join ',')"
$a=@(($values)); "parenthesized=$($a.Count)|values=$($a -join ',')"
$a=@(Write-Output -InputObject $values); "command=$($a.Count)|values=$($a -join ',')"
$a=@($none,1); "literal=$($a.Count)|values=$($a -join ',')"
""", [new("Output", "expression=2|values=,1"), new("Output", "trailingSemicolon=2|values=,1"), new("Output", "statements=2|values=1,2"), new("Output", "parenthesized=2|values=,1"), new("Output", "command=2|values=,1"), new("Output", "literal=2|values=,1")]),

            ("no output casts distinguish object string and numeric conversion", """
function Empty {}
$none=Empty
$x=[object]$none; "objectNull=$($null -eq $x)|capture=$(@($x).Count)"
$x=[string]$none; "string=$($x -is [string])|capture=$(@($x).Count)|value=<$x>"
$x=[int]$none; "int=$($x -is [int])|capture=$(@($x).Count)|value=$x"
$x=$none ?? 'fallback'; "coalesced=$x|capture=$(@($x).Count)"
$x=$none; $x ??= 7; "assigned=$x|capture=$(@($x).Count)"
""", [new("Output", "objectNull=True|capture=0"), new("Output", "string=False|capture=1|value=<>"), new("Output", "int=True|capture=1|value=0"), new("Output", "coalesced=fallback|capture=1"), new("Output", "assigned=7|capture=1")]),

            ("explicit casts and as distinguish no output from explicit null", """
function Empty {}
$none=Empty
$x=[object]$none; "object:none=$(@($x).Count)|null=$($null -eq $x)"
$x=[object[]]$none; "objectArray:none=$(@($x).Count)|null=$($null -eq $x)"
$x=[string]$none; "stringNone:null=$($null -eq $x)|string=$($x -is [string])|capture=$(@($x).Count)"
$x=[string]$null; "stringNull:null=$($null -eq $x)|string=$($x -is [string])|capture=$(@($x).Count)"
$x=$none -as [string]; "asNone:null=$($null -eq $x)|string=$($x -is [string])|capture=$(@($x).Count)"
$x=$null -as [string]; "asNull:null=$($null -eq $x)|string=$($x -is [string])|capture=$(@($x).Count)"
$count=0; foreach ($item in $null) { $count++ }; "foreachNull=$count"
$count=0; foreach ($item in $none) { $count++ }; "foreachNone=$count"
""", [new("Output", "object:none=0|null=True"), new("Output", "objectArray:none=1|null=True"), new("Output", "stringNone:null=True|string=False|capture=1"), new("Output", "stringNull:null=False|string=True|capture=1"), new("Output", "asNone:null=False|string=True|capture=1"), new("Output", "asNull:null=False|string=True|capture=1"), new("Output", "foreachNull=0"), new("Output", "foreachNone=0")]),

            ("where predicates use complete success output and preserve side streams", """
$a=@(1 | Where-Object { $false; $false }); "multipleFalse=$($a.Count)|value=$($a[0])"
$a=@(1 | Where-Object { Write-Warning 'predicate-warning' }); "warningOnly=$($a.Count)"
$a=@(1 | Where-Object { ,@($false) }); "nestedFalse=$($a.Count)"
$a=@(1 | Where-Object { $null }); "null=$($a.Count)"
$a=@(1 | Where-Object { @() }); "empty=$($a.Count)"
""", [new("Output", "multipleFalse=1|value=1"), new("Warning", "predicate-warning"), new("Output", "warningOnly=0"), new("Output", "nestedFalse=0"), new("Output", "null=0"), new("Output", "empty=0")]),

            ("list truthiness distinguishes direct and nested collection values", """
$single=[System.Collections.Generic.List[object]]@($false)
if ($single) { 'single-true' } else { 'single-false' }
$many=[System.Collections.Generic.List[object]]@($false,$false)
if ($many) { 'many-true' } else { 'many-false' }
$nested=,$single
if ($nested) { 'nested-true' } else { 'nested-false' }
$a=@(1 | Where-Object { Write-Output -NoEnumerate $single }); "predicateSingle=$($a.Count)"
$a=@(1 | Where-Object { Write-Output -NoEnumerate $many }); "predicateMany=$($a.Count)"
""", [new("Output", "single-false"), new("Output", "many-true"), new("Output", "nested-true"), new("Output", "predicateSingle=0"), new("Output", "predicateMany=1")]),

            ("measure and group ignore null pipeline entries", """
$a=@($null,1,$null,1)
$m=$a | Measure-Object -Sum
"count=$($m.Count)|sum=$($m.Sum)"
$g=@($a | Group-Object)
"groups=$($g.Count)|count=$($g[0].Count)|name=$($g[0].Name)"
$m=$null | Measure-Object
"nullCount=$($m.Count)"
$g=@($null | Group-Object)
"nullGroups=$($g.Count)"
""", [new("Output", "count=2|sum=2"), new("Output", "groups=1|count=2|name=1"), new("Output", "nullCount=0"), new("Output", "nullGroups=0")]),

            ("member projection emits collection elements with distinct null handling", """
@{A=@(1,2)} | ForEach-Object A
@{A=@(3,4)} | Select-Object -ExpandProperty A
$a=@(@{A=$null} | ForEach-Object A); "foreachNull=$($a.Count)|firstNull=$($null -eq $a[0])"
$a=@(@{A=$null} | Select-Object -ExpandProperty A); "expandNull=$($a.Count)"
""", [new("Output", "1"), new("Output", "2"), new("Output", "3"), new("Output", "4"), new("Output", "foreachNull=1|firstNull=True"), new("Output", "expandNull=0")]),

            ("selection and sorting skip explicit null pipeline inputs", """
$a=@($null,2,$null,1 | Select-Object); "selected=$($a.Count)|values=$($a -join ',')"
$a=@($null,2,$null,1 | Select-Object -First 1); "first=$($a.Count)|values=$($a -join ',')"
$a=@($null,2,$null,1 | Sort-Object); "sorted=$($a.Count)|values=$($a -join ',')"
""", [new("Output", "selected=2|values=2,1"), new("Output", "first=1|values=2"), new("Output", "sorted=2|values=1,2")]),

            ("json conversion enumerates arrays unless no enumerate is requested", """
ConvertFrom-Json '[1,2]'
$a=@(ConvertFrom-Json '[3,4]' -NoEnumerate)
"outer=$($a.Count)|inner=$($a[0].Count)|values=$($a[0] -join ',')"
$a=@(ConvertFrom-Json 'null'); "null=$($a.Count)|firstNull=$($null -eq $a[0])"
$a=@(ConvertFrom-Json 'null' -NoEnumerate); "protectedNull=$($a.Count)|firstNull=$($null -eq $a[0])"
""", [new("Output", "1"), new("Output", "2"), new("Output", "outer=1|inner=2|values=3,4"), new("Output", "null=1|firstNull=True"), new("Output", "protectedNull=1|firstNull=True")]),

            ("out string stream omits null entries while retaining text", """
$a=@($null,'a',$null,'b' | Out-String -Stream)
"count=$($a.Count)|values=$($a -join ',')"
""", [new("Output", "count=2|values=a,b")]),

            ("multiple positional write output arguments retain nested array shape", """
Write-Output @(1,2) @(3,4) | ForEach-Object { "array=$($_ -is [array])|count=$($_.Count)|values=$($_ -join ',')" }
$a=@(Write-Output -NoEnumerate @(1,2) @(3,4))
"outer=$($a.Count)|arguments=$($a[0].Count)|first=$($a[0][0] -join ',')|second=$($a[0][1] -join ',')"
""", [new("Output", "array=True|count=2|values=1,2"), new("Output", "array=True|count=2|values=3,4"), new("Output", "outer=1|arguments=2|first=1,2|second=3,4")]),

            ("list add void results differ from arraylist returned indices", """
$list=[System.Collections.Generic.List[int]]@(1,2)
$v=$list.Add(3); "void=$(@($v).Count)|list=$($list -join ',')"
$list.Add(4)
'after-list-add'
$mutable=[System.Collections.ArrayList]@(1,2)
$v=$mutable.Add(3); "index=$v|capture=$(@($v).Count)|list=$($mutable -join ',')"
""", [new("Output", "void=0|list=1,2,3"), new("Output", "after-list-add"), new("Output", "index=2|capture=1|list=1,2,3")]),

            ("void list mutation methods have no success output", """
$list=[System.Collections.Generic.List[int]]@(1,2)
$v=$list.Insert(1,3); "insert=$(@($v).Count)|list=$($list -join ',')"
$v=$list.RemoveAt(0); "removeAt=$(@($v).Count)|list=$($list -join ',')"
$v=$list.Clear(); "clear=$(@($v).Count)|count=$($list.Count)"
$mutable=[System.Collections.ArrayList]@(1,2)
$v=$mutable.Insert(1,3); "mutableInsert=$(@($v).Count)|list=$($mutable -join ',')"
$v=$mutable.RemoveAt(0); "mutableRemoveAt=$(@($v).Count)|list=$($mutable -join ',')"
$v=$mutable.Clear(); "mutableClear=$(@($v).Count)|count=$($mutable.Count)"
""", [new("Output", "insert=0|list=1,3,2"), new("Output", "removeAt=0|list=3,2"), new("Output", "clear=0|count=0"), new("Output", "mutableInsert=0|list=1,3,2"), new("Output", "mutableRemoveAt=0|list=3,2"), new("Output", "mutableClear=0|count=0")]),

            ("generic list removal returns success while arraylist removal is void", """
$list=[System.Collections.Generic.List[int]]@(1,1,2)
$r=$list.Remove(1); "removed=$r|bool=$($r -is [bool])|list=$($list -join ',')"
$r=$list.Remove(9); "missing=$r|list=$($list -join ',')"
$mutable=[System.Collections.ArrayList]@(1,1,2)
$r=$mutable.Remove(1); "mutableVoid=$(@($r).Count)|list=$($mutable -join ',')"
$r=$mutable.Remove(9); "mutableMissing=$(@($r).Count)|list=$($mutable -join ',')"
""", [new("Output", "removed=True|bool=True|list=1,2"), new("Output", "missing=False|list=1,2"), new("Output", "mutableVoid=0|list=1,2"), new("Output", "mutableMissing=0|list=1,2")]),

            ("write host remains information outside success captures and predicates", """
$r=$(Write-Host 'host'; 1)
"result=$r|count=$(@($r).Count)"
$r=@(1 | Where-Object { Write-Host 'predicate-host' })
"matches=$($r.Count)"
""", [new("Information", "host"), new("Output", "result=1|count=1"), new("Information", "predicate-host"), new("Output", "matches=0")]),

            ("write host captures information even when visibility is ignored", """
$InformationPreference='SilentlyContinue'
$r=$(Write-Host 'visible' -InformationVariable captured; 7)
"result=$r|captured=$($captured.Count)|message=$($captured[0])"
Write-Host 'suppressed' -InformationAction Ignore -InformationVariable captured
"ignored=$($captured.Count)|message=$($captured[0])"
Write-Host 'still-visible' -InformationAction SilentlyContinue -InformationVariable captured
"silentCaptured=$($captured.Count)|message=$($captured[0])"
$InformationPreference='Ignore'
Write-Host 'preference-suppressed' -InformationVariable captured
"preferenceCaptured=$($captured.Count)|message=$($captured[0])"
""", [new("Information", "visible"), new("Output", "result=7|captured=1|message=visible"), new("Output", "ignored=1|message=suppressed"), new("Information", "still-visible"), new("Output", "silentCaptured=1|message=still-visible"), new("Output", "preferenceCaptured=1|message=preference-suppressed")]),

            ("write host stop emits and captures information before terminating", """
try { Write-Host 'before-stop' -InformationAction Stop -InformationVariable captured; 'unreachable' } catch { 'caught-command' }
"captured=$($captured.Count)|message=$($captured[0])"
$InformationPreference='Stop'
try { Write-Host 'preference-stop' -InformationVariable captured; 'unreachable' } catch { 'caught-preference' }
"captured=$($captured.Count)|message=$($captured[0])"
""", [new("Information", "before-stop"), new("Output", "caught-command"), new("Output", "captured=1|message=before-stop"), new("Information", "preference-stop"), new("Output", "caught-preference"), new("Output", "captured=1|message=preference-stop")]),

            ("write host null is an empty information record rather than success output", """
$r=$(Write-Host $null -InformationVariable captured)
"success=$(@($r).Count)|captured=$($captured.Count)|message=<$($captured[0])>"
""", [new("Information", ""), new("Output", "success=0|captured=1|message=<>")]),

            ("foreach scriptblocks preserve ordered records and partial output before throw", """
1 | ForEach-Object { 'before'; Write-Warning 'middle'; 'after' }
try { 1 | ForEach-Object { 'partial'; Write-Warning 'before-throw'; throw 'boom'; 'unreachable' } } catch { 'caught' }
""", [new("Output", "before"), new("Warning", "middle"), new("Output", "after"), new("Output", "partial"), new("Warning", "before-throw"), new("Output", "caught")]),

            ("failed nested success capture forwards side streams without leaking partial values", """
$r='old'
try { $r=@(1 | ForEach-Object { 'partial'; Write-Warning 'inside-failed-capture'; throw 'boom' }) } catch { 'caught' }
"result=$r"
""", [new("Warning", "inside-failed-capture"), new("Output", "caught"), new("Output", "result=old")]),

            ("source functions preserve partial records before terminating pipeline execution", """
function Emit { 'source-value'; Write-Warning 'source-warning'; throw 'boom' }
try { Emit | ForEach-Object { "processed=$_" } } catch { 'caught-source' }
""", [new("Output", "processed=source-value"), new("Warning", "source-warning"), new("Output", "caught-source")]),

            ("null handling follows each output consumer contract", """
$a=@(@{A=@($null,1)} | Select-Object -ExpandProperty A)
"expanded=$($a.Count)|values=$($a -join ',')"
$a=@($null | Select-String '^$'); "matches=$($a.Count)|emptyLine=$($a[0].Line -eq '')"
$a=@($null | Format-List); "formatList=$($a.Count)"
$a=@($null | Format-Table); "formatTable=$($a.Count)"
$a=@($null | Out-String -Stream); "stream=$($a.Count)"
$a=@($null | Out-String); "plain=$($a.Count)|empty=$($a[0] -eq '')"
""", [new("Output", "expanded=1|values=1"), new("Output", "matches=1|emptyLine=True"), new("Output", "formatList=0"), new("Output", "formatTable=0"), new("Output", "stream=0"), new("Output", "plain=1|empty=True")]),

            ("json serialization distinguishes no input from null and normalizes nested markers", """
function Empty {}
$none=Empty
$a=@($none | ConvertTo-Json -Compress); "none=$($a.Count)"
$a=@($null | ConvertTo-Json -Compress); "null=$($a.Count)|json=$($a[0])"
@{A=$none} | ConvertTo-Json -Compress
""", [new("Output", "none=0"), new("Output", "null=1|json=null"), new("Output", "{\"A\":null}")]),

            ("hidden warning and information records remain available to common variable capture", """
Write-Warning 'ignored-warning' -WarningAction Ignore -WarningVariable warning
"warningCount=$($warning.Count)|warning=$($warning[0])"
Write-Information 'ignored-information' -InformationAction Ignore -InformationVariable information
"informationCount=$($information.Count)|information=$($information[0])"
Write-Warning 'silent-warning' -WarningAction SilentlyContinue -WarningVariable warning
"silentWarningCount=$($warning.Count)|warning=$($warning[0])"
""", [new("Output", "warningCount=1|warning=ignored-warning"), new("Output", "informationCount=1|information=ignored-information"), new("Output", "silentWarningCount=1|warning=silent-warning")]),

            ("function argument arrays retain identity for element mutation", """
$a=1,2
function Mutate { param($x) $x[0]=9 }
Mutate $a
$a -join ','
""", [new("Output", "9,2")]),

            ("binding replaces no output array elements in place without copying", """
function Empty {}
$none=Empty; $a=$none,1
@($a[0]).Count
function Mutate { param($x) $x[1]=2 }
Mutate $a
@($a[0]).Count
$a[1]
""", [new("Output", "0"), new("Output", "1"), new("Output", "2")]),

            ("array and hashtable type accelerators recognize only matching values", """
@(1,2) -is [array]
[byte[]]@(1,2) -is [System.Array]
@{A=1} -is [hashtable]
@{A=1} -is [System.Collections.Hashtable]
[System.Collections.Generic.List[object]]@(1) -is [array]
[pscustomobject]@{A=1} -is [hashtable]
$null -is [array]
""", [new("Output", "True"), new("Output", "True"), new("Output", "True"), new("Output", "True"), new("Output", "False"), new("Output", "False"), new("Output", "False")]),

            ("dictionary ContainsKey method remains callable when its name is shadowed", """
$h=@{ContainsKey='shadow';A=1}
$h.ContainsKey('A')
$h.ContainsKey('missing')
$h.ContainsKey
try { $h.ContainsKey() } catch { 'arity-error' }
""", [new("Output", "True"), new("Output", "False"), new("Output", "shadow"), new("Output", "arity-error")]),

        };

        var failures = new List<string>();
        foreach (var (name, script, expected) in cases)
        {
            try
            {
                var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
                if (!result.Records.SequenceEqual(expected))
                {
                    var expectedText = string.Join(Environment.NewLine, expected.Select(record => $"{record.Stream}: {record.Text}"));
                    var actualText = string.Join(Environment.NewLine, result.Records.Select(record => $"{record.Stream}: {record.Text}"));
                    failures.Add($"{name}{Environment.NewLine}Expected:{Environment.NewLine}{expectedText}" +
                        $"{Environment.NewLine}Actual:{Environment.NewLine}{actualText}");
                }
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
            }
        }

        Console.WriteLine($"Output capture scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Output capture failures:" + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }
}

