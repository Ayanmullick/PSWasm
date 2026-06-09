using PSWasm;

var tests = new (string Name, Func<ValueTask> Run)[]
{
    ("operators and expressions", VerifyOperatorsAsync),
    ("stream records", VerifyStreamRecordsAsync),
    ("browser-safe built-ins", VerifyBuiltInsAsync),
    ("splatting and pipeline", VerifySplattingAndPipelineAsync),
    ("object pipeline commands", VerifyObjectPipelineCommandsAsync),
    ("json commands", VerifyJsonCommandsAsync),
    ("sort and measure commands", VerifySortAndMeasureCommandsAsync),
    ("group and output commands", VerifyGroupAndOutputCommandsAsync)
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

static async ValueTask VerifyBuiltInsAsync()
{
    var result = await ExecuteAsync("""
Get-Date -Format 'yyyy'
Get-TimeZone
""");

    if (result.Records.Count != 2)
    {
        Fail($"Expected 2 built-in records, got {result.Records.Count}.");
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

static async ValueTask VerifyJsonCommandsAsync()
{
    var result = await ExecuteAsync("""
@{Name='one'; Value=2; Tags=@('a','b')} | ConvertTo-Json -Compress
'{"Name":"two","Value":3}' | ConvertFrom-Json | Select-Object -ExpandProperty Name
'[1,2,3]' | ConvertFrom-Json | Where-Object { $_ -gt 1 }
""");

    ExpectLines(result, [
        "{\"Name\":\"one\",\"Value\":2,\"Tags\":[\"a\",\"b\"]}",
        "two",
        "2",
        "3"
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
