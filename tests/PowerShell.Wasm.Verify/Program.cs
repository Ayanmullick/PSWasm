using PSWasm;

var tests = new (string Name, Func<ValueTask> Run)[]
{
    ("operators and expressions", VerifyOperatorsAsync),
    ("array basics", VerifyArrayBasicsAsync),
    ("variable commands", VerifyVariableCommandsAsync),
    ("command discovery", VerifyCommandDiscoveryAsync),
    ("stream records", VerifyStreamRecordsAsync),
    ("pipeline chain operators", VerifyPipelineChainOperatorsAsync),
    ("browser-safe built-ins", VerifyBuiltInsAsync),
    ("splatting and pipeline", VerifySplattingAndPipelineAsync),
    ("object pipeline commands", VerifyObjectPipelineCommandsAsync),
    ("format commands", VerifyFormatCommandsAsync),
    ("json and csv commands", VerifyJsonAndCsvCommandsAsync),
    ("sort and measure commands", VerifySortAndMeasureCommandsAsync),
    ("group and output commands", VerifyGroupAndOutputCommandsAsync),
    ("region comments", VerifyRegionCommentsAsync),
    ("try catch finally", VerifyTryCatchFinallyAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static async ValueTask VerifyOperatorsAsync()
{
    var result = await ExecuteAsync("""
2 + 2
($var = 1 + 2) -eq 3
$var
1..3
'abc' -replace @('b','x')
@('red','blue') -contains 'blue'
'blue' -in @('red','blue')
@('a','b') -join '-'
'a b c' -split ' '
'{0}-{1}' -f @('left','right')
$missing ?? 'fallback'
""");

    ExpectLines(result, [
        "4",
        "True",
        "3",
        "1",
        "2",
        "3",
        "axc",
        "True",
        "True",
        "a-b",
        "a",
        "b",
        "c",
        "left-right",
        "fallback"
    ]);
}

static async ValueTask VerifyStreamRecordsAsync()
{
    var result = await ExecuteAsync("""
Write-Output 'out'
Write-Warning 'warn'
Write-Error 'err'
Write-Information 'info'
""");

    ExpectRecords(result, [
        new("Output", "out"),
        new("Warning", "warn"),
        new("Error", "err"),
        new("Information", "info")
    ]);
}

static async ValueTask VerifyArrayBasicsAsync()
{
    var result = await ExecuteAsync("""
$a = 22,5,10,8,12
$a.Count
$a.Length
$a[0]
$a[-1]
$a[1..3]
$a[0,2]
$single = ,7
$single.Count
@("Hello World").Count
@().Count
$range = 5..8
$range
($a + @(99,100)).Count
""");

    ExpectLines(result, [
        "5",
        "5",
        "22",
        "12",
        "5",
        "10",
        "8",
        "22",
        "10",
        "1",
        "1",
        "0",
        "5",
        "6",
        "7",
        "8",
        "7"
    ]);
}

static async ValueTask VerifyPipelineChainOperatorsAsync()
{
    var result = await ExecuteAsync("""
Write-Output 'First' && Write-Output 'Second'
Write-Error 'BadAnd' && Write-Output 'SkippedAnd'
Write-Output 'FirstOr' || Write-Output 'SkippedOr'
Write-Error 'BadOr' || Write-Output 'SecondOr'
Write-Output 'Pipe' | ForEach-Object { $_ + 'Ok' } && Write-Output 'AfterPipe'
Write-Output 'NewLineLeft' &&
    Write-Output 'NewLineRight'
try {
    throw 'ChainStop' && Write-Output 'SkippedThrow'
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "First",
        "Second",
        "[Error] BadAnd",
        "FirstOr",
        "[Error] BadOr",
        "SecondOr",
        "PipeOk",
        "AfterPipe",
        "NewLineLeft",
        "NewLineRight",
        "ChainStop"
    ]);
}

static async ValueTask VerifyCommandDiscoveryAsync()
{
    var result = await ExecuteAsync("""
Get-Command Format-* | Select-Object -ExpandProperty Name
gcm sv | Select-Object -ExpandProperty CommandType
Get-Command -Name Get-Variable | Select-Object -ExpandProperty Name
""");

    ExpectLines(result, [
        "Format-List",
        "Format-Table",
        "BrowserCommand",
        "Get-Variable"
    ]);
}

static async ValueTask VerifyVariableCommandsAsync()
{
    var result = await ExecuteAsync("""
Set-Variable -Name BrowserName -Value 'PSWasm'
Get-Variable BrowserName -ValueOnly
$BrowserName
Set-Variable Count 3
Get-Variable Count | Select-Object -ExpandProperty Value
Clear-Variable BrowserName
Get-Variable BrowserName | Format-List Name Value
Remove-Variable Count
Get-Variable Count
""");

    ExpectLines(result, [
        "PSWasm",
        "PSWasm",
        "3",
        "Name  : BrowserName",
        "Value : "
    ]);
}

static async ValueTask VerifyBuiltInsAsync()
{
    var result = await ExecuteAsync("""
Get-Date -Format 'yyyy'
Get-TimeZone
Get-Culture
Get-UICulture
""");

    if (result.Records.Count != 4)
    {
        Fail($"Expected 4 built-in records, got {result.Records.Count}.");
    }

    if (result.Records[0].Stream != "Output" || result.Records[0].Text.Length != 4 || !result.Records[0].Text.All(char.IsDigit))
    {
        Fail($"Unexpected Get-Date output: '{result.Records[0].Text}'.");
    }

    if (!result.Records[1].Text.Contains("Id=", StringComparison.Ordinal) ||
        !result.Records[1].Text.Contains("BaseUtcOffset=", StringComparison.Ordinal))
    {
        Fail($"Unexpected Get-TimeZone output: '{result.Records[1].Text}'.");
    }

    for (var i = 2; i <= 3; i++)
    {
        if (!result.Records[i].Text.Contains("Name=", StringComparison.Ordinal) ||
            !result.Records[i].Text.Contains("DisplayName=", StringComparison.Ordinal) ||
            !result.Records[i].Text.Contains("LCID=", StringComparison.Ordinal))
        {
            Fail($"Unexpected culture output: '{result.Records[i].Text}'.");
        }
    }
}

static async ValueTask VerifySplattingAndPipelineAsync()
{
    var result = await ExecuteAsync("""
$Out = @{InputObject= 'Splat works'}
Write-Output @Out
1 | Write-Output
""");

    ExpectLines(result, ["Splat works", "1"]);
}

static async ValueTask VerifyObjectPipelineCommandsAsync()
{
    var result = await ExecuteAsync("""
1..4 | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 10 }
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Where-Object { $PSItem.Value -ge 2 } |
    Select-Object -ExpandProperty Name
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Select-Object -First 2 Name
""");

    ExpectLines(result, [
        "30",
        "40",
        "two",
        "three",
        "@{Name=one}",
        "@{Name=two}"
    ]);
}

static async ValueTask VerifyJsonAndCsvCommandsAsync()
{
    var result = await ExecuteAsync("""
@{Name='one'; Value=2; Tags=@('a','b')} | ConvertTo-Json -Compress
'{"Name":"two","Value":3}' | ConvertFrom-Json | Select-Object -ExpandProperty Name
'[1,2,3]' | ConvertFrom-Json | Where-Object { $_ -gt 1 }
'Name,Value
csv,4
"quoted, name",5' | ConvertFrom-Csv | Select-Object -ExpandProperty Name
@('Name;Value','semi;6') | ConvertFrom-Csv -Delimiter ';' | Select-Object -ExpandProperty Value
'explicit,7' | ConvertFrom-Csv -Header @('Name','Value') | Select-Object -ExpandProperty Value
""");

    ExpectLines(result, [
        "{\"Name\":\"one\",\"Value\":2,\"Tags\":[\"a\",\"b\"]}",
        "two",
        "2",
        "3",
        "csv",
        "quoted, name",
        "6",
        "7"
    ]);
}

static async ValueTask VerifyFormatCommandsAsync()
{
    var result = await ExecuteAsync("""
@(@{Name='one'; Value=1}, @{Name='two'; Value=20}) | Format-Table Name Value
@{Name='list'; Value=3} | Format-List
'Name,Value
csv,4' | ConvertFrom-Csv | Format-List Name Value
@(@{Name='hide'; Value=5}) | Format-Table Name Value -HideTableHeaders
""");

    ExpectLines(result, [
        "Name  Value",
        "----  -----",
        "one   1",
        "two   20",
        "Name  : list",
        "Value : 3",
        "Name  : csv",
        "Value : 4",
        "hide  5"
    ]);
}

static async ValueTask VerifySortAndMeasureCommandsAsync()
{
    var result = await ExecuteAsync("""
@(@{Name='three'; Value=3}, @{Name='one'; Value=1}, @{Name='two'; Value=2}) |
    Sort-Object Value |
    Select-Object -ExpandProperty Name
@('b','a','b') | Sort-Object -Unique
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Measure-Object Value -Sum -Average -Minimum -Maximum |
    ConvertTo-Json -Compress
""");

    ExpectLines(result, [
        "one",
        "two",
        "three",
        "a",
        "b",
        "{\"Count\":3,\"Property\":\"Value\",\"Sum\":6,\"Average\":2,\"Minimum\":1,\"Maximum\":3}"
    ]);
}

static async ValueTask VerifyGroupAndOutputCommandsAsync()
{
    var result = await ExecuteAsync("""
@('b','a','b') |
    Group-Object |
    Sort-Object Name |
    Select-Object Count Name |
    ConvertTo-Json -Compress
@(@{Kind='fruit'; Name='apple'}, @{Kind='veg'; Name='kale'}, @{Kind='fruit'; Name='pear'}) |
    Group-Object Kind |
    Sort-Object Name |
    Select-Object Count Name |
    ConvertTo-Json -Compress
@{Name='one'; Value=1} | Out-String
@('x','y') | Out-String
""");

    ExpectLines(result, [
        "[{\"Count\":1,\"Name\":\"a\"},{\"Count\":2,\"Name\":\"b\"}]",
        "[{\"Count\":2,\"Name\":\"fruit\"},{\"Count\":1,\"Name\":\"veg\"}]",
        "@{Name=one; Value=1}",
        "x",
        "y"
    ]);
}

static async ValueTask VerifyRegionCommentsAsync()
{
    var result = await ExecuteAsync("""
#region math
Write-Output (2 + 2)
#endregion
# region data
@{Name='region'; Value=5} | ConvertTo-Json -Compress
# endregion
""");

    ExpectLines(result, [
        "4",
        "{\"Name\":\"region\",\"Value\":5}"
    ]);
}

static async ValueTask VerifyTryCatchFinallyAsync()
{
    var result = await ExecuteAsync("""
try {
    throw 'boom'
} catch {
    $_.Message
} finally {
    'cleanup'
}
try {
    'ok'
} catch {
    'bad'
} finally {
    'done'
}
""");

    ExpectLines(result, [
        "boom",
        "cleanup",
        "ok",
        "done"
    ]);
}

static ValueTask<PowerShellWasmResult> ExecuteAsync(string script)
{
    var runtime = new PowerShellWasmRuntime();
    return runtime.ExecuteAsync(script);
}

static void ExpectLines(PowerShellWasmResult result, string[] expected)
{
    var actual = result.Text.Split(Environment.NewLine, StringSplitOptions.None);
    if (!actual.SequenceEqual(expected))
    {
        Fail($"Expected lines:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
    }
}

static void ExpectRecords(PowerShellWasmResult result, PowerShellWasmOutputRecord[] expected)
{
    if (!result.Records.SequenceEqual(expected))
    {
        var expectedText = string.Join(Environment.NewLine, expected.Select(static record => $"{record.Stream}: {record.Text}"));
        var actualText = string.Join(Environment.NewLine, result.Records.Select(static record => $"{record.Stream}: {record.Text}"));
        Fail($"Expected records:{Environment.NewLine}{expectedText}{Environment.NewLine}Actual:{Environment.NewLine}{actualText}");
    }
}

static void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.ExitCode = 1;
    throw new InvalidOperationException(message);
}
