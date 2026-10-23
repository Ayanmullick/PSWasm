using PSWasm;

internal static class CallbackVerification
{
    public static async ValueTask VerifyAsync()
    {
        AssertIncompleteCallbackDiagnostic();
        // Expected records were independently recorded with native PowerShell 7.6.1.
        // Command callbacks, calculated expressions, and intrinsic methods have distinct invocation scopes and input.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("foreach parameter not pipeline", """
1,2 | ForEach-Object { param($item) "item=$item;under=$_;alias=$PSItem;args=$($args.Count);bound=$($PSBoundParameters.Count)" }
""", [new("Output", "item=;under=1;alias=1;args=0;bound=0"), new("Output", "item=;under=2;alias=2;args=0;bound=0")]),
            ("where parameter not pipeline", """
1,2 | Where-Object { param($item) $null -eq $item }
""", [new("Output", "1"), new("Output", "2")]),
            ("foreach default each item", """
1,2 | ForEach-Object { param($item='fallback') "item=$item;under=$_;bound=$($PSBoundParameters.Count)" }
""", [new("Output", "item=fallback;under=1;bound=0"), new("Output", "item=fallback;under=2;bound=0")]),
            ("where default each item", """
1,2 | Where-Object { param($item=3) $item -gt $_ }
""", [new("Output", "1"), new("Output", "2")]),
            ("foreach default reads current", """
1,2 | ForEach-Object { param($item=$_ + 10) "item=$item;under=$_" }
""", [new("Output", "item=11;under=1"), new("Output", "item=12;under=2")]),
            ("where default reads current", """
1,2 | Where-Object { param($item=$_ * 2) $item -eq 4 }
""", [new("Output", "2")]),
            ("foreach typed default", """
'a','b' | ForEach-Object { param([int]$number,[string]$text,[switch]$flag) "n=$number;t=$text;flag=$flag;args=$($args.Count)" }
""", [new("Output", "n=0;t=;flag=False;args=0"), new("Output", "n=0;t=;flag=False;args=0")]),
            ("where typed default", """
'a','b' | Where-Object { param([int]$number,[switch]$flag) ($number -eq 0) -and (-not $flag) }
""", [new("Output", "a"), new("Output", "b")]),
            ("foreach explicit switch default", """
1,2 | ForEach-Object { param([switch]$flag=$true) "flag=$flag;bound=$($PSBoundParameters.Count)" }
""", [new("Output", "flag=True;bound=0"), new("Output", "flag=True;bound=0")]),
            ("foreach outer arguments hidden", """
function Invoke-Example { param($first) 1,2 | ForEach-Object { "args=$($args.Count);bound=$($PSBoundParameters.Count);hasFirst=$($PSBoundParameters.ContainsKey('first'))" } }; Invoke-Example 9 8 7
""", [new("Output", "args=0;bound=1;hasFirst=True"), new("Output", "args=0;bound=1;hasFirst=True")]),
            ("where outer arguments hidden", """
function Invoke-Example { param($first) 1,2 | Where-Object { ($args.Count -eq 0) -and ($PSBoundParameters.Count -eq 0) }; "outer=$($args.Count):$($PSBoundParameters.Count)" }; Invoke-Example 9 8 7
""", [new("Output", "outer=2:1")]),
            ("foreach input once", """
1,2 | ForEach-Object { "first=$($input -join ',');second=$($input -join ',');under=$_" }
""", [new("Output", "first=1;second=1;under=1"), new("Output", "first=2;second=2;under=2")]),
            ("where input once", """
1,2 | Where-Object { $value=@($input); ($value.Count -eq 1) -and ($value[0] -eq 2) -and (@($input).Count -eq 0) }
""", []),
            ("foreach input array item", """
Write-Output -NoEnumerate @(1,2) | ForEach-Object { $values=@($input); "count=$($values.Count);first=$($values[0] -join ',');under=$($_ -join ',')" }
""", [new("Output", "count=1;first=1,2;under=1,2")]),
            ("foreach scope mutations", """
$total=0; 1,2 | ForEach-Object { $total += $_; $created='yes' }; "total=$total;created=$created"
""", [new("Output", "total=3;created=yes")]),
            ("where scope mutations", """
$total=0; $values=1,2 | Where-Object { $total += $_; $created='yes'; $_ -eq 2 }; "total=$total;created=$created;values=$values"
""", [new("Output", "total=3;created=yes;values=2")]),
            ("foreach parameter restores outer", """
$item='outer'; 1,2 | ForEach-Object { param($item='local') $item += $_; "inside=$item" }; "after=$item"
""", [new("Output", "inside=local1"), new("Output", "inside=local2"), new("Output", "after=local2")]),
            ("where parameter restores outer", """
$item='outer'; 1,2 | Where-Object { param($item='local') $item += $_; $item -eq 'local2' }; "after=$item"
""", [new("Output", "2"), new("Output", "after=local2")]),
            ("foreach automatic restores outer", """
$_='outer'; 1,2 | ForEach-Object { $PSItem='inner'; "alias=$_" }; "after=$_/$PSItem"
""", [new("Output", "alias=inner"), new("Output", "alias=inner"), new("Output", "after=outer/outer")]),
            ("where automatic restores outer", """
$_='outer'; 1,2 | Where-Object { $PSItem='inner'; $_ -eq 'inner' }; "after=$_/$PSItem"
""", [new("Output", "1"), new("Output", "2"), new("Output", "after=outer/outer")]),
            ("foreach begin process end", """
$_='outer'; 1,2 | ForEach-Object -Begin { param($p='begin') "B=$p;u=$_;i=$(@($input).Count);a=$($args.Count)" } -Process { param($p='process') "P=$p;u=$_;i=$($input -join ',');a=$($args.Count)" } -End { param($p='end') "E=$p;u=$_;i=$(@($input).Count);a=$($args.Count)" }; "after=$_"
""", [new("Output", "B=begin;u=outer;i=0;a=0"), new("Output", "P=process;u=1;i=1;a=0"), new("Output", "P=process;u=2;i=2;a=0"), new("Output", "E=end;u=outer;i=0;a=0"), new("Output", "after=outer")]),
            ("foreach begin process end shared", """
1,2 | ForEach-Object -Begin { $total=0 } -Process { $total += $_ } -End { "total=$total" }; "after=$total"
""", [new("Output", "total=3"), new("Output", "after=3")]),
            ("foreach positional blocks", """
1,2 | ForEach-Object { 'begin' } { param($p='process') "$p=$_" } { 'end' }
""", [new("Output", "begin"), new("Output", "process=1"), new("Output", "process=2"), new("Output", "end")]),
            ("foreach no pipeline", """
ForEach-Object { param($p='default') "p=$p;u=$_;input=$(@($input).Count);args=$($args.Count)" }
""", [new("Output", "p=default;u=;input=1;args=0")]),
            ("foreach empty pipeline", """
@() | ForEach-Object -Begin { 'begin' } -Process { 'process' } -End { 'end' }
""", [new("Output", "begin"), new("Output", "end")]),
            ("foreach null pipeline", """
$null | ForEach-Object { param($p='default') "p=$p;null=$($null -eq $_);input=$(@($input).Count)" }
""", [new("Output", "p=default;null=True;input=1")]),
            ("where null pipeline", """
$values=@($null | Where-Object { param($p='default') $p -eq 'default' }); "count=$($values.Count);null=$($null -eq $values[0])"
""", [new("Output", "count=1;null=True")]),
            ("nested foreach automatics", """
1,2 | ForEach-Object { param($p='outer') "before=$_/$p"; 3,4 | ForEach-Object { param($p='inner') "inside=$_/$p" }; "after=$_/$p" }
""", [new("Output", "before=1/outer"), new("Output", "inside=3/inner"), new("Output", "inside=4/inner"), new("Output", "after=1/inner"), new("Output", "before=2/outer"), new("Output", "inside=3/inner"), new("Output", "inside=4/inner"), new("Output", "after=2/inner")]),
            ("nested where foreach", """
1,2 | ForEach-Object { $outer=$_; $values=3,4 | Where-Object { param($p=4) $_ -eq $p }; "outer=$outer;under=$_;values=$values" }
""", [new("Output", "outer=1;under=1;values=4"), new("Output", "outer=2;under=2;values=4")]),
            ("foreach return is callback local", """
1,2 | ForEach-Object { if ($_ -eq 1) { return 'first' }; 'second' }; 'after'
""", [new("Output", "first"), new("Output", "second"), new("Output", "after")]),
            ("where return is callback local", """
1,2 | Where-Object { if ($_ -eq 1) { return $false }; return $true }; 'after'
""", [new("Output", "2"), new("Output", "after")]),
            ("foreach throw finally restore", """
$_='outer'; try { 1,2 | ForEach-Object { if ($_ -eq 2) { throw 'stop' }; $_ } } catch { "caught=$($_.Exception.Message)" }; "after=$_"
""", [new("Output", "1"), new("Output", "caught=stop"), new("Output", "after=outer")]),
            ("where throw finally restore", """
$_='outer'; try { 1,2 | Where-Object { if ($_ -eq 2) { throw 'stop' }; $true } } catch { "caught=$($_.Exception.Message)" }; "after=$_"
""", [new("Output", "1"), new("Output", "caught=stop"), new("Output", "after=outer")]),
            ("foreach default return", """
1,2 | ForEach-Object { param($p=$(return 'default-return')) "p=$p;u=$_" }; 'after'
""", [new("Output", "p=default-return;u=1"), new("Output", "p=default-return;u=2"), new("Output", "after")]),
            ("foreach default throw", """
try { 1,2 | ForEach-Object { param($p=$(throw 'default-error')) 'body' } } catch { "caught=$($_.Exception.Message)" }; 'after'
""", [new("Output", "caught=default-error"), new("Output", "after")]),
            ("where side stream", """
1,2 | Where-Object { Write-Warning "item=$_"; $_ -eq 2 }
""", [new("Warning", "item=1"), new("Warning", "item=2"), new("Output", "2")]),
            ("foreach bound parameters restored", """
function Invoke-Example { param($first) 1,2 | ForEach-Object { param($second='inner') "inner=$($PSBoundParameters.Count)" }; "outer=$($PSBoundParameters.Count):$($PSBoundParameters['first'])" }; Invoke-Example -first 9
""", [new("Output", "inner=0"), new("Output", "inner=0"), new("Output", "outer=1:9")]),
            ("foreach outer arguments param", """
function Invoke-Example { param($first) 1,2 | ForEach-Object { param($second='inner') "args=$($args.Count);bound=$($PSBoundParameters.Count);hasFirst=$($PSBoundParameters.ContainsKey('first'))" } }; Invoke-Example 9 8 7
""", [new("Output", "args=0;bound=0;hasFirst=False"), new("Output", "args=0;bound=0;hasFirst=False")]),
            ("where outer arguments param", """
function Invoke-Example { param($first) 1,2 | Where-Object { param($second='inner') ($args.Count -eq 0) -and ($PSBoundParameters.Count -eq 0) }; "outer=$($args.Count):$($PSBoundParameters.Count)" }; Invoke-Example 9 8 7
""", [new("Output", "1"), new("Output", "2"), new("Output", "outer=2:1")]),
            ("foreach new parameter persists", """
1,2 | ForEach-Object { param($p='local') $p += $_; "inside=$p" }; "after=$p"
""", [new("Output", "inside=local1"), new("Output", "inside=local2"), new("Output", "after=")]),
            ("where new parameter persists", """
1,2 | Where-Object { param($p='local') $p += $_; $p -eq 'local2' }; "after=$p"
""", [new("Output", "2"), new("Output", "after=")]),
            ("foreach input member type", """
1,2 | ForEach-Object { "inputType=$($input.GetType().Name);underType=$($_.GetType().Name);inputCount=$(@($input).Count)" }
""", [new("Output", "inputType=Object[];underType=Int32;inputCount=1"), new("Output", "inputType=Object[];underType=Int32;inputCount=1")]),
            ("select callback parameter default", """
1,2 | Select-Object @{Name='Value';Expression={ param($p='default') "$p/$_/$($args.Count)/$($PSBoundParameters.Count)/$($input -join ',')" }} | ForEach-Object { $_.Value }
""", [new("Output", "default/1/0/0/"), new("Output", "default/2/0/0/")]),
            ("select callback scope", """
$total=0; 1,2 | Select-Object @{Name='Value';Expression={ $total += $_; $created='yes'; $total }} | ForEach-Object { $_.Value }; "after=$total/$created"
""", [new("Output", "1"), new("Output", "2"), new("Output", "after=0/")]),
            ("sort callback parameter default", """
1,2 | Sort-Object { param($p=3) $p - $_ }
""", [new("Output", "2"), new("Output", "1")]),
            ("group callback parameter default", """
1,2 | Group-Object { param($p='default') "$p/$($input -join ',')" } | ForEach-Object { "$($_.Name):$($_.Count)" }
""", [new("Output", "default/:2")]),
            ("measure callback parameter default", """
1,2 | Measure-Object -Property { param($p=10) $p + $_ } -Sum | ForEach-Object { $_.Sum }
""", [new("Output", "23")]),
            ("intrinsic foreach parameters", """
(1,2).ForEach({ param($p='default') "$p/$_/$($args.Count)/$($PSBoundParameters.Count)/$($input -join ',')" })
""", [new("Output", "default/1/0/0/"), new("Output", "default/2/0/0/")]),
            ("intrinsic where parameters", """
(1,2).Where({ param($p=3) $p -gt $_ })
""", [new("Output", "1"), new("Output", "2")]),
            ("intrinsic foreach scope", """
$total=0; (1,2).ForEach({ $total += $_; $created='yes'; $total }); "after=$total/$created"
""", [new("Output", "1"), new("Output", "3"), new("Output", "after=3/yes")]),
            ("intrinsic where scope", """
$total=0; (1,2).Where({ $total += $_; $created='yes'; $_ -eq 2 }); "after=$total/$created"
""", [new("Output", "2"), new("Output", "after=3/yes")]),
            ("intrinsic foreach input repeated", """
(1,2).ForEach({ "first=$($input -join ',');second=$($input -join ',')" })
""", [new("Output", "first=;second="), new("Output", "first=;second=")]),
            ("intrinsic where input", """
(1,2).Where({ @($input).Count -eq 0 })
""", [new("Output", "1"), new("Output", "2")]),
            ("intrinsic foreach argument", """
(1,2).ForEach({ param($p='default') "$p/$_/$($args.Count)/$($PSBoundParameters.Count)" }, 'explicit')
""", [new("Output", "explicit/1/0/1"), new("Output", "explicit/2/0/1")]),
            ("intrinsic foreach inherits input", """
function Invoke-Example { (1,2).ForEach({ "under=$_;input=$($input -join ',')" }) }; 7,8 | Invoke-Example
""", [new("Output", "under=1;input=7 8"), new("Output", "under=2;input=")]),
            ("intrinsic where inherits input", """
function Invoke-Example { (1,2).Where({ ($input -join ',') -eq '7,8' }) }; 7,8 | Invoke-Example
""", []),
            ("foreach input type", """
1,2 | ForEach-Object { "type=$($input.GetType().Name);count=$($input.Count);first=$($input[0]);second=$($input[0])" }
""", [new("Output", "type=Object[];count=1;first=1;second=1"), new("Output", "type=Object[];count=1;first=2;second=2")]),
            ("where input type", """
1,2 | Where-Object { ($input.GetType().Name -eq 'Object[]') -and ($input.Count -eq 1) -and ($input[0] -eq 2) }
""", [new("Output", "2")]),
            ("foreach empty param inherits bound", """
function Invoke-Example { param($first) 1,2 | ForEach-Object { param() "count=$($PSBoundParameters.Count)" } }; Invoke-Example -first 9
""", [new("Output", "count=1"), new("Output", "count=1")]),
            ("select inherits outer bound", """
function Invoke-Example { param($first) 1,2 | Select-Object @{Name='Value';Expression={ "count=$($PSBoundParameters.Count)" }} | ForEach-Object { $_.Value } }; Invoke-Example -first 9
""", [new("Output", "count=1"), new("Output", "count=1")]),
            ("sort scope isolated", """
$total=0; 1,2 | Sort-Object { $total += $_; $total }; "after=$total"
""", [new("Output", "1"), new("Output", "2"), new("Output", "after=0")]),
            ("foreach defaults later reads outer", """
$b='outer'; 1,2 | ForEach-Object { param($a=$b,$b='new') "$a/$b" }; "after=$b"
""", [new("Output", "outer/new"), new("Output", "new/new"), new("Output", "after=new")]),
            ("foreach default self reads outer", """
$a='outer'; 1 | ForEach-Object { param($a=$a) $a }
""", [new("Output", "outer")]),
            ("foreach default reads outer bound", """
function Invoke-Example { param($first) 1,2 | ForEach-Object { param($p=$PSBoundParameters['first']) "p=$p;count=$($PSBoundParameters.Count)" } }; Invoke-Example -first 9
""", [new("Output", "p=9;count=0"), new("Output", "p=9;count=0")]),
            ("foreach default reads earlier bound", """
1,2 | ForEach-Object { param($a=$_,$b=$a + 1) "$a/$b" }
""", [new("Output", "1/2"), new("Output", "2/3")]),
            ("foreach default scope mutation", """
$value='outer'; 1 | ForEach-Object { param($p=$($value='changed'; $value)) "p=$p;value=$value" }; "after=$value"
""", [new("Output", "p=changed;value=changed"), new("Output", "after=changed")]),
            ("intrinsic foreach switch positional", """
(1,2).ForEach({param([switch]$s,$x) "s=$s/x=$x/args=$($args.Count)/bound=$($PSBoundParameters.Count)"},$true,'x')
""", [new("Output", "s=True/x=x/args=0/bound=2"), new("Output", "s=True/x=x/args=0/bound=2")]),
            ("foreach no pipeline inherits under", """
$_='outer'; ForEach-Object { "under=$_;input=$($input.Count);null=$($null -eq $input[0])" }; "after=$_"
""", [new("Output", "under=outer;input=1;null=True"), new("Output", "after=outer")]),
            ("foreach explicit null clears under", """
$_='outer'; $null | ForEach-Object { "under=$_;input=$($input.Count);null=$($null -eq $input[0])" }; "after=$_"
""", [new("Output", "under=;input=1;null=True"), new("Output", "after=outer")]),
            ("empty object array type", """
$value=@(); "name=$($value.GetType().Name);full=$($value.GetType().FullName);count=$($value.Count)"
""", [new("Output", "name=Object[];full=System.Object[];count=0")]),
            ("single object array type", """
$value=@(1); "name=$($value.GetType().Name);full=$($value.GetType().FullName);count=$($value.Count)"
""", [new("Output", "name=Object[];full=System.Object[];count=1")]),
            ("multiple object array type", """
$value=@(1,2); "name=$($value.GetType().Name);full=$($value.GetType().FullName);count=$($value.Count)"
""", [new("Output", "name=Object[];full=System.Object[];count=2")]),
            ("typed integer array type", """
$value=[int[]]@(1,2); "name=$($value.GetType().Name);full=$($value.GetType().FullName);count=$($value.Count)"
""", [new("Output", "name=Int32[];full=System.Int32[];count=2")]),
            ("typed string array type", """
$value=[string[]]@('one'); "name=$($value.GetType().Name);full=$($value.GetType().FullName);count=$($value.Count)"
""", [new("Output", "name=String[];full=System.String[];count=1")]),
            ("measure callback scope", """
$total=0; 1,2 | Measure-Object -Property { $total += $_; $_ * 2 } -Sum | ForEach-Object { "count=$($_.Count);sum=$($_.Sum)" }; "after=$total"
""", [new("Output", "count=2;sum=6"), new("Output", "after=0")]),
            ("measure callback default input", """
1,2 | Measure-Object -Property { param($p=10) $p + $_ + @($input).Count } -Sum | ForEach-Object { "count=$($_.Count);sum=$($_.Sum)" }
""", [new("Output", "count=2;sum=23")]),
            ("measure callback count only", """
1,2 | Measure-Object -Property { param($p=10) $p + $_ } | ForEach-Object { "count=$($_.Count);sum=$($_.Sum)" }
""", [new("Output", "count=2;sum=")]),
            ("foreach defaults see caller args", """
function Invoke-Example { param($n) 1 | ForEach-Object { param($a=$args.Count,$b=$PSBoundParameters.Count) "$a/$b/$($args.Count)/$($PSBoundParameters.Count)" } }; Invoke-Example 42 extra
""", [new("Output", "1/1/0/0")]),
            ("foreach parameter global restores scope", """
$p='global'; function Invoke-Example { 1 | ForEach-Object { param($p='cb') "in=$p" }; "after=$p" }; Invoke-Example; "global=$p"
""", [new("Output", "in=cb"), new("Output", "after=global"), new("Output", "global=global")]),
            ("foreach omitted parameter validation", """
try { 1 | ForEach-Object { param([ValidateSet('yes')]$choice) 'body' } } catch { 'rejected' }; 'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("intrinsic foreach inherited input restored", """
$input='before'; (1).ForEach({ $input='during'; "in=$input" }); "after=$input"
""", [new("Output", "in=during"), new("Output", "after=before")]),
            ("intrinsic where inherited input restored", """
$input='before'; (1).Where({ $input='during'; $true }); "after=$input"
""", [new("Output", "1"), new("Output", "after=before")]),
            ("foreach begin end automatic restored", """
$_='before'; 1 | ForEach-Object -Begin { $_='begin' } -Process { "in=$_" } -End { "end=$_" }; "after=$_"
""", [new("Output", "in=1"), new("Output", "end=before"), new("Output", "after=before")]),
            ("foreach same name default reads ancestor", """
$p='outer'; function Invoke-Example { 1 | ForEach-Object { param($p=$p) "in=$p" }; "after=$p" }; Invoke-Example; "global=$p"
""", [new("Output", "in=outer"), new("Output", "after=outer"), new("Output", "global=outer")]),
            ("measure callback multiple output counts per record", """
1,2 | Measure-Object -Property { $_; $_ + 10 } | ForEach-Object { "count=$($_.Count)" }
""", [new("Output", "count=2")]),
            ("measure callback empty output numeric summary", """
1,2 | Measure-Object -Property {} -Sum -Average -Minimum -Maximum | ForEach-Object { "count=$($_.Count);sum=$($_.Sum);average=$($_.Average);min=$($_.Minimum);max=$($_.Maximum)" }
""", [new("Output", "count=2;sum=0;average=0;min=0;max=0")]),
            ("measure callback explicit null numeric summary", """
1,2 | Measure-Object -Property {$null} -Sum -Average -Minimum -Maximum | ForEach-Object { "count=$($_.Count);sum=$($_.Sum);average=$($_.Average);min=$($_.Minimum);max=$($_.Maximum)" }
""", [new("Output", "count=2;sum=0;average=0;min=;max=")]),
            ("measure callback property preserves spaces", """
$metric=1,2 | Measure-Object -Property {  $_ * 2  } -Sum; "property=[$($metric.Property)];sum=$($metric.Sum)"
""", [new("Output", "property=[  $_ * 2  ];sum=6")]),
            ("measure callback property preserves nested source", """
$metric=1,2 | Measure-Object -Property { if ($_ -gt 1) { 'two {quoted}' } else { "one}" } }; "property=[$($metric.Property)];count=$($metric.Count)"
""", [new("Output", "property=[ if ($_ -gt 1) { 'two {quoted}' } else { \"one}\" } ];count=2")]),
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
        Console.WriteLine($"Callback scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static void AssertIncompleteCallbackDiagnostic()
    {
        Exception? failure = null;
        try { new PSWasm.Language.PowerShellWasmParser().Parse("1 | ForEach-Object { $_"); }
        catch (Exception exception) { failure = exception; }
        if (failure is not InvalidOperationException ||
            !failure.Message.StartsWith("Expected token RBrace", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An unclosed callback must produce the missing-closing-brace parse diagnostic; " +
                $"received {failure?.GetType().Name ?? "no exception"}: {failure?.Message}", failure);
        }
        Console.WriteLine("Callback parse diagnostics: 1/1 passed.");
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
