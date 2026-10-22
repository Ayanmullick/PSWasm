using System.Text.RegularExpressions;
using PSWasm;

internal static class ReturnVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records were independently recorded with native PowerShell 7.6.1.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, string? ErrorPattern)[]
        {
            ("bare return exits script after prior output", """
'before';return;'after'
""", [new("Output", "before")], null),
            ("literal return exits script", """
'before';return 'value';'after'
""", [new("Output", "before"), new("Output", "value")], null),
            ("function return permits caller continuation", """
function Test-Return{'before';return 'value';'after'};Test-Return;'caller'
""", [new("Output", "before"), new("Output", "value"), new("Output", "caller")], null),
            ("direct command operand emits its records", """
function Test-Return{return Write-Output 'a','b';'after'};Test-Return
""", [new("Output", "a"), new("Output", "b")], null),
            ("pipeline operand transforms all command output", """
function Test-Return{return Write-Output 'a','b' | ForEach-Object { "<$_>" };'after'};Test-Return
""", [new("Output", "<a>"), new("Output", "<b>")], null),
            ("expression-started pipeline operand is accepted", """
function Test-Return{return 1,2,3 | ForEach-Object {$_*2}};Test-Return
""", [new("Output", "2"), new("Output", "4"), new("Output", "6")], null),
            ("complete successful pipeline chain belongs to return", """
function Test-Return{return Write-Output 'first' && Write-Output 'second';'after'};Test-Return
""", [new("Output", "first"), new("Output", "second")], null),
            ("failed pipeline chain fallback belongs to return", """
function Test-Return{return Write-Error 'quiet' -ErrorAction SilentlyContinue || Write-Output 'fallback';'after'};Test-Return
""", [new("Output", "fallback")], null),
            ("successful chain skips failure fallback", """
function Test-Return{return Write-Output 'first' || Write-Output 'bad'};Test-Return
""", [new("Output", "first")], null),
            ("failed chain skips conditional success", """
function Test-Return{return Write-Error 'quiet' -ErrorAction SilentlyContinue && Write-Output 'bad'};@(Test-Return).Count
""", [new("Output", "0")], null),
            ("command call operator is a return operand", """
function Test-Return{return & {'a';'b'};'after'};Test-Return
""", [new("Output", "a"), new("Output", "b")], null),
            ("named call operator is a return operand", """
function Test-Return{$command='Write-Output';return & $command 'named'};Test-Return
""", [new("Output", "named")], null),
            ("splatting in return command operands", """
function Test-Return{$options=@{InputObject=@('a','b')};return Write-Output @options};Test-Return
""", [new("Output", "a"), new("Output", "b")], null),
            ("return assignment emits assigned value", """
function Test-Return{return $value=4};Test-Return
""", [new("Output", "4")], null),
            ("return compound assignment emits assigned value", """
function Test-Return{$value=2;return $value+=3};Test-Return
""", [new("Output", "5")], null),
            ("return member assignment emits assigned value", """
$state=@{value=1};function Test-Return{return $state.value=7};Test-Return;"state=$($state.value)"
""", [new("Output", "7"), new("Output", "state=7")], null),
            ("return array assignment emits each assigned element", """
function Test-Return{return $value=1,2,3};Test-Return
""", [new("Output", "1"), new("Output", "2"), new("Output", "3")], null),
            ("return parenthesized assignment retains output", """
function Test-Return{return ($value=4)};Test-Return
""", [new("Output", "4")], null),
            ("return arithmetic expression", """
function Test-Return{return 2+3*4};Test-Return
""", [new("Output", "14")], null),
            ("return unary comma keeps nested array as one pipeline object", """
function Test-Return{return ,(1,2)};Test-Return | ForEach-Object { "count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')" }
""", [new("Output", "count=2|array=True|value=1,2")], null),
            ("return array enumerates exactly one level", """
function Test-Return{return @(@(1,2),@(3,4))};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=2|array=True|value=1,2"), new("Output", "count=2|array=True|value=3,4")], null),
            ("direct command return preserves no enumerate arrays", """
function Test-Return{return Write-Output -NoEnumerate @(1,2)};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=2|array=True|value=1,2")], null),
            ("return explicit null differs from no output", """
function Return-Null{return $null};function Return-Nothing{return};"null=$(@(Return-Null).Count)|nothing=$(@(Return-Nothing).Count)"
""", [new("Output", "null=1|nothing=0")], null),
            ("return empty array produces no output", """
function Test-Return{return @()};"count=$(@(Test-Return).Count)"
""", [new("Output", "count=0")], null),
            ("return empty string is a real output record", """
function Test-Return{return ''};"count=$(@(Test-Return).Count)|value=[$(Test-Return)]"
""", [new("Output", "count=1|value=[]")], null),
            ("return command with no output produces no record", """
function Test-Return{return Write-Output @()};"count=$(@(Test-Return).Count)"
""", [new("Output", "count=0")], null),
            ("return literal false and zero remain records", """
function Return-False{return $false};function Return-Zero{return 0};Return-False;Return-Zero
""", [new("Output", "False"), new("Output", "0")], null),
            ("return hashtable stays one object", """
function Test-Return{return @{A=1;B=2}};Test-Return | ForEach-Object {"count=$($_.Count)|a=$($_.A)|b=$($_.B)"}
""", [new("Output", "count=2|a=1|b=2")], null),
            ("direct return command streams producer state", """
$state=@{step=0};function Source{$state.step=1;1;$state.step=2;2};function Test-Return{return Source};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=1"), new("Output", "item=2|step=2")], null),
            ("parenthesized return command buffers producer state", """
$state=@{step=0};function Source{$state.step=1;1;$state.step=2;2};function Test-Return{return (Source)};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=2"), new("Output", "item=2|step=2")], null),
            ("subexpression return operand buffers producer state", """
$state=@{step=0};function Source{$state.step=1;1;$state.step=2;2};function Test-Return{return $(Source)};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=2"), new("Output", "item=2|step=2")], null),
            ("array subexpression return operand buffers producer state", """
$state=@{step=0};function Source{$state.step=1;1;$state.step=2;2};function Test-Return{return @(Source)};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=2"), new("Output", "item=2|step=2")], null),
            ("return pipeline streams between nested callbacks", """
$state=@{step=0};function Test-Return{return 1,2 | ForEach-Object {$state.step=$_;$_}};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=1"), new("Output", "item=2|step=2")], null),
            ("direct return preserves partial output before failure", """
function Test-Return{return & {'partial';throw 'failed'}};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "caught=failed")], null),
            ("parenthesized return discards partial output before failure", """
function Test-Return{return (& {'partial';throw 'failed'})};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "caught=failed")], null),
            ("subexpression return discards partial output before failure", """
function Test-Return{return $('partial';throw 'failed')};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "caught=failed")], null),
            ("return failure executes finally before outer catch", """
function Test-Return{try{return & {'partial';throw 'failed'}}finally{'cleanup'}};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "cleanup"), new("Output", "caught=failed")], null),
            ("return emits value before finally output", """
function Test-Return{try{return 'value'}finally{'cleanup'}};Test-Return
""", [new("Output", "value"), new("Output", "cleanup")], null),
            ("finally sees literal return success state", """
function Test-Return{Write-Error 'quiet' -ErrorAction SilentlyContinue;try{return 1}finally{"status=$?"}};Test-Return
""", [new("Output", "1"), new("Output", "status=False")], null),
            ("finally sees bare return prior status", """
function Test-Return{Write-Error 'quiet' -ErrorAction SilentlyContinue;try{return}finally{"status=$?"}};Test-Return
""", [new("Output", "status=False")], null),
            ("finally sees successful command return status", """
function Test-Return{Write-Error 'quiet' -ErrorAction SilentlyContinue;try{return Write-Output 1}finally{"status=$?"}};Test-Return
""", [new("Output", "1"), new("Output", "status=True")], null),
            ("finally sees failed command return status", """
function Test-Return{try{return Write-Error 'quiet' -ErrorAction SilentlyContinue}finally{"status=$?"}};Test-Return
""", [new("Output", "status=False")], null),
            ("catch does not catch successful return", """
function Test-Return{try{return 'value'}catch{'bad'}finally{'cleanup'}};Test-Return
""", [new("Output", "value"), new("Output", "cleanup")], null),
            ("return inside catch exits function after cleanup", """
function Test-Return{try{throw 'original'}catch{return $_.Exception.Message}finally{'cleanup'};'after'};Test-Return
""", [new("Output", "original"), new("Output", "cleanup")], null),
            ("return in finally cannot override pending throw", """
function Test-Return{try{throw 'original'}finally{return 'replacement'}};try{Test-Return}catch{'bad'}
""", [], "Flow of control cannot leave a Finally block|ControlLeavingFinally"),
            ("return in finally cannot replace pending return", """
function Test-Return{try{return 'original'}finally{return 'replacement'}};Test-Return
""", [], "Flow of control cannot leave a Finally block|ControlLeavingFinally"),
            ("throw in finally keeps already returned output", """
function Test-Return{try{return 'original'}finally{throw 'replacement'}};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "original"), new("Output", "caught=replacement")], null),
            ("return command cleanup preserves streaming timing", """
$state=@{step=0};function Test-Return{try{return Write-Output 1,2}finally{$state.step=9}};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"};"after=$($state.step)"
""", [new("Output", "item=1|step=0"), new("Output", "item=2|step=0"), new("Output", "after=9")], null),
            ("return ends each process callback only", """
1,2,3 | ForEach-Object {if($_ -eq 2){return 'middle'};"item=$_"};'caller'
""", [new("Output", "item=1"), new("Output", "middle"), new("Output", "item=3"), new("Output", "caller")], null),
            ("return command ends each process callback only", """
1,2 | ForEach-Object {return Write-Output $_; 'bad'};'caller'
""", [new("Output", "1"), new("Output", "2"), new("Output", "caller")], null),
            ("return in begin process and end leaves pipeline running", """
1,2 | ForEach-Object -Begin {return 'begin';'bad'} -Process {return "process=$_";'bad'} -End {return 'end';'bad'};'caller'
""", [new("Output", "begin"), new("Output", "process=1"), new("Output", "process=2"), new("Output", "end"), new("Output", "caller")], null),
            ("return in invoked block does not exit caller function", """
function Test-Return{& {return 'inner';'bad'};'outer'};Test-Return
""", [new("Output", "inner"), new("Output", "outer")], null),
            ("return in dotted block does not exit caller function", """
function Test-Return{. {return 'inner';'bad'};'outer'};Test-Return
""", [new("Output", "inner"), new("Output", "outer")], null),
            ("return from loop exits function", """
function Test-Return{foreach($item in 1,2,3){if($item -eq 2){return "stop=$item"};$item};'bad'};Test-Return
""", [new("Output", "1"), new("Output", "stop=2")], null),
            ("return from switch exits function", """
function Test-Return{switch(2){2{return 'matched'} default{'bad'}};'bad'};Test-Return
""", [new("Output", "matched")], null),
            ("return inside subexpression assignment escapes function", """
function Test-Return{$value=$(1;return 2);'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside array subexpression assignment escapes function", """
function Test-Return{$value=@(1;return 2);'after'};Test-Return
""", [], null),
            ("return inside parenthesized assignment escapes function", """
function Test-Return{($value=$(1;return 2));'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside interpolation escapes function", """
function Test-Return{$value="$(return 4)";'after'};Test-Return
""", [new("Output", "4")], null),
            ("nested return subexpression forwards returning value", """
function Test-Return{return $(return 3)};Test-Return
""", [new("Output", "3")], null),
            ("nested return array subexpression forwards returning value", """
function Test-Return{return @(return 3)};Test-Return
""", [], null),
            ("nested return command operand forwards all return output", """
function Test-Return{$value=$(return Write-Output 1,2);'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside hashtable value escapes function", """
function Test-Return{$value=@{A=$(1;return 2)};'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside array index escapes function", """
function Test-Return{$value=10,20;$value[$(1;return 2)];'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside command argument escapes function", """
function Test-Return{Write-Output $(1;return 2);'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside nested captures bypasses all capture layers", """
function Test-Return{$value="$(@($(1;return 2)))";'after'};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return operand remains live while outer selector stops upstream", """
$state=@{after=0;cleanup=0};function Test-Return{try{return & {1;$state.after=1;2}}finally{$state.cleanup=1}};Test-Return | Select-Object -First 1;"after=$($state.after)|cleanup=$($state.cleanup)"
""", [new("Output", "1"), new("Output", "after=0|cleanup=1")], null),
            ("return operand nested pipeline completes downstream end", """
function Test-Return{return Write-Output 1,2 | ForEach-Object -Process {$_*2} -End {'end'}};Test-Return
""", [new("Output", "2"), new("Output", "4"), new("Output", "end")], null),
            ("return successful function resets caller status", """
function Test-Return{return Write-Error 'quiet' -ErrorAction SilentlyContinue};Test-Return;"status=$?"
""", [new("Output", "status=True")], null),
            ("return expression uses original status before changing it", """
function Test-Return{Write-Error 'quiet' -ErrorAction SilentlyContinue;return $?};Test-Return;"status=$?"
""", [new("Output", "False"), new("Output", "status=True")], null),
            ("return side streams preserve ordering around values", """
function Test-Return{return & {Write-Warning 'warning';Write-Output 'value';Write-Information 'information' -InformationAction Continue}};Test-Return
""", [new("Warning", "warning"), new("Output", "value"), new("Information", "information")], null),
            ("return nonterminating error stream preserves ordering", """
function Test-Return{return & {Write-Output 'first';Write-Error 'middle';Write-Output 'last'}};Test-Return
""", [new("Output", "first"), new("Error", "middle"), new("Output", "last")], null),
            ("single statement assignment flushes return output", """
function Test-Return{$value=if($true){1;return 2};3};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("parallel statement assignment discards return output", """
function Test-Return{$x,$y=if($true){1;return 2};3};Test-Return
""", [], null),
            ("return from condition subexpression forwards output", """
function Test-Return{if($(1;return 2)){3};4};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return from command condition operand discards output", """
function Test-Return{if(Write-Output $(1;return 2)){3};4};Test-Return
""", [], null),
            ("unassigned interpolation forwards returning output", """
function Test-Return{"hello$(1;return 2)world";3};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("bare prefix increment return emits no output", """
function Test-Return{$x=1;return ++$x};"count=$(@(Test-Return).Count)"
""", [new("Output", "count=0")], null),
            ("parenthesized prefix increment return emits value", """
function Test-Return{$x=1;return (++$x)};Test-Return
""", [new("Output", "2")], null),
            ("bare postfix increment return emits no output", """
function Test-Return{$x=1;return $x++};"count=$(@(Test-Return).Count)"
""", [new("Output", "count=0")], null),
            ("parenthesized postfix increment return emits prior value", """
function Test-Return{$x=1;return ($x++)};Test-Return
""", [new("Output", "1")], null),
            ("parameter default subexpression return supplies default and runs body", """
function Test-Return{param($value=$(return 1));"body=$value"};Test-Return;'caller'
""", [new("Output", "body=1"), new("Output", "caller")], null),
            ("script block parameter default subexpression return", """
& {param($value=$(return 1));"body=$value"};'caller'
""", [new("Output", "body=1"), new("Output", "caller")], null),
            ("parameter default array subexpression return", """
function Test-Return{param($value=@(return 1));"body=$value"};Test-Return;'caller'
""", [new("Output", "body="), new("Output", "caller")], null),
            ("return in nested block inside finally is local", """
function Test-Return{try{'try'}finally{& {return 'inner';'bad'};'cleanup'}};Test-Return
""", [new("Output", "try"), new("Output", "inner"), new("Output", "cleanup")], null),
            ("return in nested function inside finally is local", """
function Test-Return{try{'try'}finally{function Inner{return 'inner';'bad'};Inner;'cleanup'}};Test-Return
""", [new("Output", "try"), new("Output", "inner"), new("Output", "cleanup")], null),
            ("nested assignment subexpressions flush each pending value", """
function Test-Return{$x=$(1;$y=$(2;return 3);4);5};Test-Return
""", [new("Output", "1"), new("Output", "2"), new("Output", "3")], null),
            ("process callback capture return flushes per item", """
1,2 | ForEach-Object {$value=$(10;return $_);99};100
""", [new("Output", "10"), new("Output", "1"), new("Output", "10"), new("Output", "2"), new("Output", "100")], null),
            ("return forwarding from subexpression runs finally afterward", """
function Test-Return{try{$value=$(1;return 2);3}finally{4};5};Test-Return
""", [new("Output", "1"), new("Output", "2"), new("Output", "4")], null),
            ("parenthesized command condition forwards return capture", """
function Test-Return{if((Write-Output $(1;return 2))){3};4};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("array around parenthesized command preserves return capture", """
function Test-Return{@((Write-Output $(1;return 2)));4};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("parallel expression assignment forwards return capture", """
function Test-Return{$a,$b=$(1;return 2);3};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("parallel command assignment discards return capture", """
function Test-Return{$a,$b=Write-Output $(1;return 2);3};Test-Return
""", [], null),
            ("single command assignment forwards return capture", """
function Test-Return{$a=Write-Output $(1;return 2);3};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside for initializer forwards capture", """
function Test-Return{for($x=$(1;return 2);$true;$x++){3};4};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("return inside for iterator forwards capture after body", """
function Test-Return{for($x=0;$x -lt 1;$x=$(1;return 2)){3};4};Test-Return
""", [new("Output", "3"), new("Output", "1"), new("Output", "2")], null),
            ("return inside throw command operand replaces pending throw", """
function Test-Return{throw Write-Output $(1;return 2);3};Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("interpolated parameter default return provides raw output", """
function Test-Return{param($value="prefix$('a';return 'b')suffix");"body=$($value -join ',')"};Test-Return;'caller'
""", [new("Output", "body=a,b"), new("Output", "caller")], null),
            ("later parameter defaults evaluate after first default returns", """
function Test-Return{param($first=$(return 1),$second=$(2));"body=$first,$second"};Test-Return;'caller'
""", [new("Output", "body=1,2"), new("Output", "caller")], null),
            ("parameter supplied argument bypasses returning default", """
function Test-Return{param($value=$(return 1));"body=$value"};Test-Return 7;'caller'
""", [new("Output", "body=7"), new("Output", "caller")], null),
            ("return inside default invoked block is local to block", """
function Test-Return{param($value=$(& {return 3}));"body=$value"};Test-Return;'caller'
""", [new("Output", "body=3"), new("Output", "caller")], null),
            ("function signature default cannot return from finally", """
try{}finally{function Test-Return($value=$(return 1)){$value}}
""", [], "Flow of control cannot leave a Finally block|ControlLeavingFinally"),
            ("function body parameter default return is local inside finally", """
try{}finally{function Test-Return{param($value=$(return 1));$value};Test-Return}
""", [new("Output", "1")], null),
            ("script block parameter default return is local inside finally", """
try{}finally{& {param($value=$(return 1));$value}}
""", [new("Output", "1")], null),
            ("standalone dollar capture retains partial output on failure", """
try{$('partial';throw 'failed')}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "caught=failed")], null),
            ("standalone parenthesized command retains partial output on failure", """
try{(& {'partial';throw 'failed'})}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "caught=failed")], null),
            ("command argument capture discards partial output on failure", """
try{Write-Output $('partial';throw 'failed')}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "caught=failed")], null),
            ("parallel return assignment emits assigned tuple shape", """
function Test-Return{return $a,$b=1,2,3};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=1|array=False|value=1"), new("Output", "count=2|array=True|value=2,3")], null),
            ("parallel return assignment keeps nested array shape", """
function Test-Return{return $a,$b=@(1,@(2,3))};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=1|array=False|value=1"), new("Output", "count=2|array=True|value=2,3")], null),
            ("parallel return command assignment emits assigned tuple shape", """
function Test-Return{return $a,$b=Write-Output 1,2,3};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=1|array=False|value=1"), new("Output", "count=2|array=True|value=2,3")], null),
            ("parenthesized parallel assignment return emits assigned tuple shape", """
function Test-Return{return ($a,$b=1,2,3)};Test-Return | ForEach-Object {"count=$($_.Count)|array=$($_ -is [array])|value=$($_ -join ',')"}
""", [new("Output", "count=1|array=False|value=1"), new("Output", "count=2|array=True|value=2,3")], null),
            ("standalone dollar capture buffers success before delivery", """
$state=@{step=0};function Test-Return{$($state.step=1;1;$state.step=2;2;$state.step=3)};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=3"), new("Output", "item=2|step=3")], null),
            ("standalone parenthesized command buffers success before delivery", """
$state=@{step=0};function Test-Return{(& {$state.step=1;1;$state.step=2;2;$state.step=3})};Test-Return | ForEach-Object {"item=$_|step=$($state.step)"}
""", [new("Output", "item=1|step=3"), new("Output", "item=2|step=3")], null),
            ("single expression array around dollar capture preserves partial error output", """
try{$value=@($('partial';throw 'failed'))}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "caught=failed")], null),
            ("return single expression array capture preserves partial error output", """
function Test-Return{return @($('partial';throw 'failed'))};try{Test-Return}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "partial"), new("Output", "caught=failed")], null),
            ("multiple statement array capture discards partial error output", """
try{$value=@('outer';$('partial';throw 'failed'))}catch{"caught=$($_.Exception.Message)"}
""", [new("Output", "caught=failed")], null),
            ("returning array default supplies null rather than no output", """
function Test-Return{param($value=@(return 1));"count=$(@($value).Count)|null=$($null -eq $value)"};Test-Return
""", [new("Output", "count=1|null=True")], null),
            ("empty subexpression default supplies null", """
function Test-Return{param($value=$());"count=$(@($value).Count)|null=$($null -eq $value)"};Test-Return
""", [new("Output", "count=1|null=True")], null),
            ("bare return default supplies null", """
function Test-Return{param($value=$(return));"count=$(@($value).Count)|null=$($null -eq $value)"};Test-Return
""", [new("Output", "count=1|null=True")], null),
            ("break default supplies null and runs body", """
function Test-Return{param($value=$(break));"count=$(@($value).Count)|null=$($null -eq $value)"};Test-Return;'caller'
""", [new("Output", "count=1|null=True"), new("Output", "caller")], null),
            ("continue default supplies null and runs body", """
function Test-Return{param($value=$(continue));"count=$(@($value).Count)|null=$($null -eq $value)"};Test-Return;'caller'
""", [new("Output", "count=1|null=True"), new("Output", "caller")], null),
            ("literal subexpression return updates status through inner statement", """
function Test-Return{Write-Error 'quiet' -ErrorAction SilentlyContinue;try{return $(1)}finally{"status=$?"}};Test-Return
""", [new("Output", "1"), new("Output", "status=True")], null),
            ("quoted command subexpression preserves embedded hash", """
"$(Write-Output foo#bar)"
""", [new("Output", "foo#bar")], null),
            ("quoted command subexpression preserves embedded block comment marker", """
"$(Write-Output foo<#bar)"
""", [new("Output", "foo<#bar")], null),
            ("quoted subexpression reads line comments at token boundaries", """
"$(1; # ignored
 2)"
""", [new("Output", "1 2")], null),
            ("quoted subexpression reads block comments at token boundaries", """
"$(1; <# comment #> 2)"
""", [new("Output", "1 2")], null),
            ("quoted subexpression counts balanced parentheses inside block comment", """
"$(1; <# () #> 2)"
""", [new("Output", "1 2")], null),
            ("escaped dollar does not introduce a return subexpression", """
"literal `$(return 1)"
""", [new("Output", "literal $(return 1)")], null),
            ("return after quoted subexpression line comment cannot leave finally", """
try{}finally{if($false){"$(1; # ignored
 return 2)"}}
""", [], "Flow of control cannot leave a Finally block|ControlLeavingFinally"),
            ("embedded hash in finally interpolation remains literal command text", """
try{}finally{"$(Write-Output foo#bar)" }
""", [new("Output", "foo#bar")], null),
            ("dot sourced return preserves caller variable mutations", """
$value=1;. {$value=2;return 'inner';'bad'};"value=$value"
""", [new("Output", "inner"), new("Output", "value=2")], null),
            ("dot sourced parameters bind in caller scope", """
. {param($first,$second);"inside=$first,$second";return 'done'} 1 2;"after=$first,$second"
""", [new("Output", "inside=1,2"), new("Output", "done"), new("Output", "after=1,2")], null),
            ("dot sourced script block variable returns its output", """
$block={param($value);return "value=$value";'bad'};. $block 7;'caller'
""", [new("Output", "value=7"), new("Output", "caller")], null),
            ("dot sourced argument list is bound without parameter declarations", """
. {"count=$($args.Count)|args=$($args -join ',')";return 'done'} 1 2;'caller'
""", [new("Output", "count=2|args=1,2"), new("Output", "done"), new("Output", "caller")], null),
            ("dot sourced return remains local inside process callback", """
1,2 | ForEach-Object {. {return "dot=$_"};"after=$_"}
""", [new("Output", "dot=1"), new("Output", "after=1"), new("Output", "dot=2"), new("Output", "after=2")], null),
            ("dot sourced block receives complete pipeline input", """
1,2 | . {"input=$($input -join ',')";return 'done'}
""", [new("Output", "input=1 2"), new("Output", "done")], null),
            ("dot sourced default return supplies parameter value", """
. {param($value=$(return 3));"body=$value"};'caller'
""", [new("Output", "body=3"), new("Output", "caller")], null),
            ("dot sourced return inside finally stays local", """
try{'try'}finally{. {return 'inner';'bad'};'cleanup'}
""", [new("Output", "try"), new("Output", "inner"), new("Output", "cleanup")], null),
            ("dot sourcing supports registered command names", """
. 'Write-Output' 'registered'
""", [new("Output", "registered")], null),
            ("dot sourced parameter changes persist while arguments restore", """
function Test-Return{param($value);"before=$value|args=$($args -join ',')";. {param($value);"inside=$value|args=$($args -join ',')";return} 7 8;"after=$value|args=$($args -join ',')"};Test-Return 1 2
""", [new("Output", "before=1|args=2"), new("Output", "inside=7|args=8"), new("Output", "after=7|args=2")], null),
            ("dot sourced partial binding survives later conversion failure", """
$first=0;$second=0;try{. {param($first,[int]$second);'bad'} 1 'invalid'}catch{'caught'};"after=$first,$second"
""", [new("Output", "caught"), new("Output", "after=1,0")], null),
            ("dot sourced partial binding survives later validation failure", """
$first=0;$second='old';try{. {param($first,[ValidateSet('ok')]$second);'bad'} 1 'invalid'}catch{'caught'};"after=$first,$second"
""", [new("Output", "caught"), new("Output", "after=1,old")], null),
            ("dot sourced input returns all items directly", """
1,2 | . {return $input;'bad'}
""", [new("Output", "1"), new("Output", "2")], null),
            ("invoked block input returns all items directly", """
1,2 | & {return $input;'bad'}
""", [new("Output", "1"), new("Output", "2")], null),
            ("dot sourced pipeline input is consumed by array capture", """
1,2 | . {$first=@($input);"first=$($first -join ',')";$second=@($input);"second=$($second.Count)"}
""", [new("Output", "first=1,2"), new("Output", "second=0")], null),
            ("invoked block pipeline input is consumed by array capture", """
1,2 | & {$first=@($input);"first=$($first -join ',')";$second=@($input);"second=$($second.Count)"}
""", [new("Output", "first=1,2"), new("Output", "second=0")], null),
            ("dot sourced foreach input consumes its enumerator", """
1,2 | . {foreach($item in $input){"item=$item"};"remaining=$(@($input).Count)"}
""", [new("Output", "item=1"), new("Output", "item=2"), new("Output", "remaining=0")], null),
            ("invoked block foreach input consumes its enumerator", """
1,2 | & {foreach($item in $input){"item=$item"};"remaining=$(@($input).Count)"}
""", [new("Output", "item=1"), new("Output", "item=2"), new("Output", "remaining=0")], null),
            ("dot sourced input pipeline consumes its enumerator", """
1,2 | . {$input | ForEach-Object {"item=$_"};"remaining=$(@($input).Count)"}
""", [new("Output", "item=1"), new("Output", "item=2"), new("Output", "remaining=0")], null),
            ("invoked block input pipeline consumes its enumerator", """
1,2 | & {$input | ForEach-Object {"item=$_"};"remaining=$(@($input).Count)"}
""", [new("Output", "item=1"), new("Output", "item=2"), new("Output", "remaining=0")], null),
            ("function return directly emits input enumerator items", """
function Test-Return{return $input};1,2 | Test-Return
""", [new("Output", "1"), new("Output", "2")], null),
            ("function input capture consumes shared enumerator state", """
function Test-Return{$first=@($input);"first=$($first -join ',')";"remaining=$(@($input).Count)"};1,2 | Test-Return
""", [new("Output", "first=1,2"), new("Output", "remaining=0")], null),
            ("invoked input enumerator supports Current MoveNext and Reset", """
1,2 | & {"before=[$($input.Current)]";$input.MoveNext();"current=$($input.Current)";$input.Reset();"reset=[$($input.Current)]";$input | ForEach-Object {"item=$_"}}
""", [new("Output", "before=[]"), new("Output", "True"), new("Output", "current=1"), new("Output", "reset=[]"), new("Output", "item=1"), new("Output", "item=2")], null),
            ("dot sourced input enumerator supports Current MoveNext and Reset", """
1,2 | . {"before=[$($input.Current)]";$input.MoveNext();"current=$($input.Current)";$input.Reset();"reset=[$($input.Current)]";$input | ForEach-Object {"item=$_"}}
""", [new("Output", "before=[]"), new("Output", "True"), new("Output", "current=1"), new("Output", "reset=[]"), new("Output", "item=1"), new("Output", "item=2")], null),
            ("joining input enumerator uses OFS and consumes input", """
$OFS='|';1,2 | & {"joined=$($input -join ',')";"remaining=$(@($input).Count)"}
""", [new("Output", "joined=1|2"), new("Output", "remaining=0")], null),
            ("joining an array containing input enumerator preserves its state", """
1,2 | & {$array=,$input;$joined=$array -join ',';"size=$($array.Count)|nonempty=$($joined.Length -gt 0)|remaining=$(@($input).Count)"}
""", [new("Output", "size=1|nonempty=True|remaining=2")], null),
            ("joining repeated nested input enumerators does not consume them", """
1,2 | & {$array=@($input,$input);$joined=$array -join ',';"separator=$($joined -like '*,*')|nonempty=$($joined.Length -gt 1)|remaining=$(@($input).Count)"}
""", [new("Output", "separator=True|nonempty=True|remaining=2")], null),
            ("string cast consumes input enumerator with OFS", """
$OFS="|";1,2 | & {$value=[string]$input;"value=$value|remaining=$(@($input).Count)"}
""", [new("Output", "value=1|2|remaining=0")], null),
            ("typed string parameter consumes input enumerator with OFS", """
function Test-Return{param([string]$value);"value=$value"};$OFS="|";1,2 | & {Test-Return $input;"remaining=$(@($input).Count)"}
""", [new("Output", "value=1|2"), new("Output", "remaining=0")], null),
            ("underscore and PSItem syntax share one bidirectional value", """
$_="first";$PSItem;$PSItem="second";$_;$psitem="third";$_
""", [new("Output", "first"), new("Output", "second"), new("Output", "third")], null),
            ("PSItem aliases remain consistent in interpolation", """
$_="first";"direct=$_/$PSItem|nested=$($_)/$($PSItem)";$PSItem="second";"direct=$_/$PSItem|nested=$($_)/$($PSItem)"
""", [new("Output", "direct=first/first|nested=first/first"), new("Output", "direct=second/second|nested=second/second")], null),
            ("dot sourced underscore writes update PSItem then restore caller", """
$_="outer";. {$_="inner";"inside=$_/$PSItem"};"after=$_/$PSItem"
""", [new("Output", "inside=inner/inner"), new("Output", "after=outer/outer")], null),
            ("dot sourced PSItem writes update underscore then restore caller", """
$PSItem="outer";. {$PSItem="inner";"inside=$_/$PSItem"};"after=$_/$PSItem"
""", [new("Output", "inside=inner/inner"), new("Output", "after=outer/outer")], null),
            ("underscore parameter binding shares PSItem alias", """
$_="outer";& {param($_);"bound=$_/$PSItem";$_="changed";"changed=$_/$PSItem"} "value";"after=$_/$PSItem"
""", [new("Output", "bound=value/value"), new("Output", "changed=changed/changed"), new("Output", "after=outer/outer")], null),
            ("PSItem parameter binding shares underscore alias", """
$_="outer";& {param($PSItem);"bound=$_/$PSItem";$PSItem="changed";"changed=$_/$PSItem"} "value";"after=$_/$PSItem"
""", [new("Output", "bound=value/value"), new("Output", "changed=changed/changed"), new("Output", "after=outer/outer")], null),
            ("dot sourced underscore parameter restores both aliases", """
$_="outer";. {param($_);"bound=$_/$PSItem";$_="changed";"changed=$_/$PSItem"} "value";"after=$_/$PSItem"
""", [new("Output", "bound=value/value"), new("Output", "changed=changed/changed"), new("Output", "after=outer/outer")], null),
            ("dot sourced PSItem parameter restores both aliases", """
$_="outer";. {param($PSItem);"bound=$_/$PSItem";$PSItem="changed";"changed=$_/$PSItem"} "value";"after=$_/$PSItem"
""", [new("Output", "bound=value/value"), new("Output", "changed=changed/changed"), new("Output", "after=outer/outer")], null),
            ("join converts shared input separator before consuming left iterator", """
1,2 | & { "join=[$($input -join $input)]|remaining=$(@($input).Count)" }
""", [new("Output", "join=[]|remaining=0")], null),
            ("join consumes separate iterator separator while left iterator uses OFS", """
$state=@{};3,4 | & {$state.separator=$input};$OFS="|";1,2 | & {"join=[$($input -join $state.separator)]|remaining=$(@($input).Count)|separator=$(@($state.separator).Count)"}
""", [new("Output", "join=[1|2]|remaining=0|separator=0")], null),
            ("join separator alias observes shared input consumption order", """
$OFS="|";1,2 | & {$alias=$input;"join=[$($input -join $alias)]|remaining=$(@($input).Count)|alias=$(@($alias).Count)"}
""", [new("Output", "join=[]|remaining=0|alias=0")], null),
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
        Console.WriteLine($"Return scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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
