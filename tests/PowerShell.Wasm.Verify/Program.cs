using System.Net;
using System.Net.Http;
using System.Text;
using PSWasm;

var tests = new (string Name, Func<ValueTask> Run)[]
{
    ("operators and expressions", VerifyOperatorsAsync),
    ("regular expressions", VerifyRegularExpressionsAsync),
    ("array basics", VerifyArrayBasicsAsync),
    ("hashtable basics", VerifyHashtableBasicsAsync),
    ("parallel assignment", VerifyParallelAssignmentAsync),
    ("browser-safe dotnet interop", VerifyBrowserSafeDotNetInteropAsync),
    ("variable commands", VerifyVariableCommandsAsync),
    ("command discovery", VerifyCommandDiscoveryAsync),
    ("stream records", VerifyStreamRecordsAsync),
    ("invoke web request", VerifyInvokeWebRequestAsync),
    ("pipeline chain operators", VerifyPipelineChainOperatorsAsync),
    ("dom session commands", VerifyDomSessionCommandsAsync),
    ("browser-safe built-ins", VerifyBuiltInsAsync),
    ("splatting and pipeline", VerifySplattingAndPipelineAsync),
    ("object pipeline commands", VerifyObjectPipelineCommandsAsync),
    ("format commands", VerifyFormatCommandsAsync),
    ("json and csv commands", VerifyJsonAndCsvCommandsAsync),
    ("sort and measure commands", VerifySortAndMeasureCommandsAsync),
    ("group and output commands", VerifyGroupAndOutputCommandsAsync),
    ("region comments", VerifyRegionCommentsAsync),
    ("try catch finally", VerifyTryCatchFinallyAsync),
    ("if elseif else", VerifyIfElseAsync),
    ("foreach statement", VerifyForEachStatementAsync),
    ("script functions", VerifyScriptFunctionsAsync),
    ("return break continue", VerifyReturnBreakContinueAsync),
    ("while statement", VerifyWhileStatementAsync),
    ("for statement", VerifyForStatementAsync),
    ("switch statement", VerifySwitchStatementAsync),
    ("automatic variables", VerifyAutomaticVariablesAsync),
    ("preference variables", VerifyPreferenceVariablesAsync),
    ("common parameters", VerifyCommonParametersAsync),
    ("runtime session reuse", VerifyRuntimeSessionReuseAsync)
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
'abc' -replace 'b','x'
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

static async ValueTask VerifyRegularExpressionsAsync()
{
    var result = await ExecuteAsync("""
'Server-01' -match 'Server-\d\d'
$Matches[0]
'CONTOSO\jsmith' -match '(?<Domain>\w+)\\(?<User>\w+)'
$Matches['Domain']
$Matches.User
'Hello World' -replace @('(\w+) \w+', '$1 Browser')
'ABC' -cmatch '^[a-z]+$'
'ABC' -imatch '^[a-z]+$'
'a1 b22 c333' -split '\s+'
switch -Regex ('browser-42') {
    'server-\d+' { 'server' }
    'browser-(?<Id>\d+)' { 'browser-' + $Matches.Id }
    default { 'fallback' }
}
switch -Regex -CaseSensitive ('ABC') {
    '^[a-z]+$' { 'lower' }
    '^[A-Z]+$' { 'upper' }
}
@('Alpha','beta','gamma42') | Select-String -Pattern '^[a-z]+$' | Select-Object -ExpandProperty Line
@('Alpha','beta') | Select-String -Pattern '^[a-z]+$' -CaseSensitive | Select-Object -ExpandProperty Line
'one two one' | Select-String -Pattern 'one' -AllMatches | Select-Object -ExpandProperty Matches | Select-Object -ExpandProperty Value
@('Alpha','beta') | Select-String -Pattern '^a' -NotMatch | Select-Object -ExpandProperty Line
""");

    ExpectLines(result, [
        "True",
        "Server-01",
        "True",
        "CONTOSO",
        "jsmith",
        "Hello Browser",
        "False",
        "True",
        "a1",
        "b22",
        "c333",
        "browser-42",
        "upper",
        "Alpha",
        "beta",
        "beta",
        "one",
        "one",
        "beta"
    ]);
}

static async ValueTask VerifyStreamRecordsAsync()
{
    var result = await ExecuteAsync("""
$DebugPreference = 'Continue'
$InformationPreference = 'Continue'
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

static async ValueTask VerifyInvokeWebRequestAsync()
{
    var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.Equals("/data", StringComparison.OrdinalIgnoreCase))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{HeaderValue(request, "Accept")}|{HeaderValue(request, "X-Demo")}", Encoding.UTF8)
            };
            response.Headers.TryAddWithoutValidation("X-Reply", "browser");
            return response;
        }

        if (path.Equals("/echo", StringComparison.OrdinalIgnoreCase))
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var contentType = request.Content?.Headers.ContentType?.MediaType ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent($"{request.Method}:{body}:{contentType}", Encoding.UTF8)
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
            Content = new StringContent("missing", Encoding.UTF8)
        };
    });

    var runtime = new PowerShellWasmRuntime(httpMessageHandler: handler);
    var result = await runtime.ExecuteAsync("""
$response = Invoke-WebRequest -Uri 'https://example.test/data' -Headers @{Accept='application/json'; 'X-Demo'='yes'}
$response.StatusCode
$response.Content
$response.Headers['X-Reply']
$post = iwr -Uri 'https://example.test/echo' -Method Post -Body 'hello' -ContentType 'text/plain'
$post.StatusCode
$post.Content
$skip = Invoke-WebRequest 'https://example.test/missing' -SkipHttpErrorCheck
$skip.StatusCode
try {
    Invoke-WebRequest 'https://example.test/missing'
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "200",
        "application/json|yes",
        "browser",
        "201",
        "POST:hello:text/plain",
        "404",
        "Invoke-WebRequest failed with HTTP 404 Not Found."
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

static async ValueTask VerifyHashtableBasicsAsync()
{
    var result = await ExecuteAsync("""
$h = @{Name='PowerShell'; CountValue=3; Nested=@{Inner='value'}}
$h.Name
$h['Name']
$h['name']
$h['Missing'] ?? 'missing'
$h.Count
$h.Keys | Sort-Object
$h.Values.Count
$h['Name','CountValue']
$h.Nested.Inner
$shadow = @{Count='shadow'; Name='ok'}
$shadow.Count
$shadow['Count']
$shadow.Keys.Count
""");

    ExpectLines(result, [
        "PowerShell",
        "PowerShell",
        "PowerShell",
        "missing",
        "3",
        "CountValue",
        "Name",
        "Nested",
        "3",
        "PowerShell",
        "3",
        "value",
        "shadow",
        "shadow",
        "2"
    ]);
}

static async ValueTask VerifyParallelAssignmentAsync()
{
    var result = await ExecuteAsync("""
$Name,$Env,$Location = 'AzReport','Dev','NorthCentralUS'
$Name
$Env
$Location
$first,$rest = 1,2,3
$first
$rest.Count
$rest[0]
$rest[1]
$one,$two,$three = 'left','right'
$one
$two
$three ?? 'missing'
$pipeHead,$pipeTail = 1..3 | ForEach-Object { $_ * 10 }
$pipeHead
$pipeTail.Count
$pipeTail[0]
$pipeTail[1]
""");

    ExpectLines(result, [
        "AzReport",
        "Dev",
        "NorthCentralUS",
        "1",
        "2",
        "2",
        "3",
        "left",
        "right",
        "missing",
        "10",
        "2",
        "20",
        "30"
    ]);
}

static async ValueTask VerifyBrowserSafeDotNetInteropAsync()
{
    var result = await ExecuteAsync("""
$Key = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('secret'))
$StringToSign = "post`ndocs`ndbs/db/colls/cont`nSat, 13 Jun 2026 00:00:00 GMT`n`n"
$KeyType,$TokenVer = 'master','1.0'
$KeyBytes,$DataBytes = [Convert]::FromBase64String($Key),[Text.Encoding]::UTF8.GetBytes($StringToSign)
$SignatureBytes = [System.Security.Cryptography.HMACSHA256]::HashData($KeyBytes,$DataBytes)
$Signature = [Convert]::ToBase64String($SignatureBytes)
$Authorization = [Uri]::EscapeDataString("type=${KeyType}&ver=${TokenVer}&sig=$Signature")
$EscapedKey = [Uri]::EscapeDataString('+/8=')
$NormalizedKey = [Uri]::UnescapeDataString("  $EscapedKey  ").Trim()
$ConnectionKey = [Uri]::EscapeDataString('AccountEndpoint=https://acct.documents.azure.com:443/;AccountKey=+/8=;')
$ConnectionKey = [Uri]::UnescapeDataString($ConnectionKey).Trim() -replace '^["'']+|["'']+$',''
if ($ConnectionKey -match '(?:^|;)\s*AccountKey\s*=\s*([^;]+)') { $ConnectionKey = $Matches[1] }
$ConnectionKey = [Uri]::UnescapeDataString($ConnectionKey).Trim() -replace '^["'']+|["'']+$',''
$ConnectionKey = $ConnectionKey -replace '\s+',''
$KeyBytes.Length
$SignatureBytes.Length
$SignatureBytes[0]
$Signature
$Authorization
$NormalizedKey
[Convert]::ToBase64String([Convert]::FromBase64String($NormalizedKey))
$ConnectionKey
[Convert]::ToBase64String([Convert]::FromBase64String($ConnectionKey))
'MIXED'.ToLowerInvariant()
'  padded  '.Trim()
""");

    ExpectLines(result, [
        "6",
        "32",
        "47",
        "L9Jxlb3LXlKXW6cjwKQ4cTNmGIIPB6c0+bKeinhORis=",
        "type%3Dmaster%26ver%3D1.0%26sig%3DL9Jxlb3LXlKXW6cjwKQ4cTNmGIIPB6c0%2BbKeinhORis%3D",
        "+/8=",
        "+/8=",
        "+/8=",
        "+/8=",
        "mixed",
        "padded"
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

static async ValueTask VerifyDomSessionCommandsAsync()
{
    var result = await ExecuteAsync("""
$dom = New-DomSession -Name Main -Target '#app'
$dom.Id
$dom.Name
$dom.Target
$dom.State
$dom.SessionType
Get-DomSession Main | Select-Object -ExpandProperty Target
Get-DomSession -Id $dom.Id | Select-Object -ExpandProperty Name
Get-Command *-DomSession | Select-Object -ExpandProperty Name
Remove-DomSession $dom
$remaining = Get-DomSession
$remaining ?? 'none'
""");

    ExpectLines(result, [
        "1",
        "Main",
        "#app",
        "Opened",
        "Dom",
        "#app",
        "Main",
        "Get-DomSession",
        "New-DomSession",
        "Remove-DomSession",
        "none"
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
$Items = 'array', 'splat'
Write-Output @Items
function Show-Splat($Name, $Value) {
    "$Name=$Value"
}
$Named = @{Name='hash'; Value=1}
Show-Splat @Named
Show-Splat @Named -Value 2
$First = @{Name='multi'}
$Second = @{Value=3}
Show-Splat @First @Second
$Positionals = 'array', 4
Show-Splat @Positionals
function Forward-Bound($Name, $Value) {
    Show-Splat @PSBoundParameters
}
Forward-Bound -Name 'bound' -Value 5
function Forward-Args {
    Show-Splat @args
}
Forward-Args 'args' 6
1 | Write-Output
""");

    ExpectLines(result, [
        "Splat works",
        "array",
        "splat",
        "hash=1",
        "hash=2",
        "multi=3",
        "array=4",
        "bound=5",
        "args=6",
        "1"
    ]);
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

static async ValueTask VerifyIfElseAsync()
{
    var result = await ExecuteAsync("""
$value = 3
if ($value -gt 5) {
    'large'
} elseif ($value -eq 3) {
    'matched'
} else {
    'small'
}
$branch = if ($false) {
    'bad'
} else {
    'assigned'
}
$branch
if (@()) {
    'array true'
} else {
    'array false'
}
if ('text') {
    'string true'
}
""");

    ExpectLines(result, [
        "matched",
        "assigned",
        "array false",
        "string true"
    ]);
}

static async ValueTask VerifyForEachStatementAsync()
{
    var result = await ExecuteAsync("""
foreach ($number in 1..3) {
    $number * 2
}
$table = @{First='one'; Second='two'}
foreach ($key in @('First','Second')) {
    $table[$key]
}
$captured = foreach ($name in @('alpha','beta')) {
    $name + '!'
}
$captured.Count
$captured[0]
$captured[1]
foreach ($missing in @()) {
    'bad'
}
$name
""");

    ExpectLines(result, [
        "2",
        "4",
        "6",
        "one",
        "two",
        "2",
        "alpha!",
        "beta!",
        "beta"
    ]);
}

static async ValueTask VerifyScriptFunctionsAsync()
{
    var result = await ExecuteAsync("""
function Add-Prefix($Text) {
    "pre-$Text"
}
Add-Prefix 'one'
Add-Prefix -Text 'two'
function Join-Args {
    $args -join ','
}
Join-Args 'a' 'b'
$captured = Add-Prefix 'three'
$captured
Get-Command Add-Prefix | Select-Object -ExpandProperty Name
function Use-Input {
    foreach ($item in $input) {
        $item * 10
    }
}
1..2 | Use-Input
$x = 'outer'
function Scope-Test($x) {
    $x = 'inner'
    $x
}
Scope-Test 'ignored'
$x
""");

    ExpectLines(result, [
        "pre-one",
        "pre-two",
        "a,b",
        "pre-three",
        "Add-Prefix",
        "10",
        "20",
        "inner",
        "outer"
    ]);
}

static async ValueTask VerifyReturnBreakContinueAsync()
{
    var result = await ExecuteAsync("""
function First-Matches($Items) {
    foreach ($item in $Items) {
        if ($item -lt 2) {
            continue
        }
        if ($item -gt 3) {
            break
        }
        $item
    }
    return 'done'
    'skipped'
}
First-Matches @(1,2,3,4)
function Return-With-Finally {
    try {
        return 'from-return'
    } finally {
        'from-finally'
    }
    'after-return'
}
Return-With-Finally
return 'top-level-return'
'top-level-skipped'
""");

    ExpectLines(result, [
        "2",
        "3",
        "done",
        "from-return",
        "from-finally",
        "top-level-return"
    ]);
}

static async ValueTask VerifyWhileStatementAsync()
{
    var result = await ExecuteAsync("""
$i = 0
while ($i -lt 5) {
    $i = $i + 1
    if ($i -eq 2) {
        continue
    }
    if ($i -eq 5) {
        break
    }
    $i
}
$captured = while ($false) {
    'bad'
}
$captured ?? 'none'
try {
    while ($true) {
    }
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "1",
        "3",
        "4",
        "none",
        "The browser-safe while loop exceeded 10000 iterations."
    ]);
}

static async ValueTask VerifyForStatementAsync()
{
    var result = await ExecuteAsync("""
for ($i = 0; $i -lt 5; $i = $i + 1) {
    if ($i -eq 1) {
        continue
    }
    if ($i -eq 4) {
        break
    }
    $i
}
$i
for ($k = 0; $k -lt 3; $k++) {
    $k
}
$k
$k--
$k
$missingIncrement++
$missingIncrement
$missingDecrement--
$missingDecrement
$captured = for ($j = 0; $j -lt 0; $j = $j + 1) {
    'bad'
}
$captured ?? 'none'
try {
    for (;;) {
    }
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "0",
        "2",
        "3",
        "4",
        "0",
        "1",
        "2",
        "3",
        "2",
        "1",
        "-1",
        "none",
        "The browser-safe for loop exceeded 10000 iterations."
    ]);
}

static async ValueTask VerifySwitchStatementAsync()
{
    var result = await ExecuteAsync("""
switch (2) {
    1 { 'one' }
    2 { 'two' }
    default { 'other' }
}
switch ('cat') {
    'c*' { 'wild' }
    'cat' { 'exact' }
}
switch ('other') {
    'x' { 'x' }
    default { 'default' }
}
switch (@('red','blue','green','tail')) {
    'red' { 'r' }
    'blue' { continue }
    'green' { 'g'; break }
    default { 'd' }
}
$captured = switch (42) {
    40 { 'no' }
    42 { 'yes' }
}
$captured
""");

    ExpectLines(result, [
        "two",
        "wild",
        "exact",
        "default",
        "r",
        "g",
        "yes"
    ]);
}

static async ValueTask VerifyAutomaticVariablesAsync()
{
    var result = await ExecuteAsync("""
$true
$false
$null ?? 'null'
$?
Write-Error 'automatic failure'
$?
$Error.Count
$Error[0].Message
$StackTrace ?? 'no stack'
'abc123' -match '([a-z]+)(?<Digits>\d+)'
$Matches[0]
$Matches[1]
$Matches['Digits']
$PSEdition
$PSVersionTable.PSEdition
$PSVersionTable.Platform
$Host.Name
$Host.CurrentCulture -eq $PSCulture
$PWD.Path
$HOME
$PSHOME
$ShellId
$IsCoreCLR
$IsWindows
$EnabledExperimentalFeatures.Count
$NestedPromptLevel
$PSDebugContext ?? 'no debug'
function Test-Bound($Name, $Value) {
    $PSBoundParameters.Count
    $PSBoundParameters['Name']
    $PSBoundParameters['Value']
    $args.Count
    $input.Count
}
'pipe' | Test-Bound -Name 'browser' 42
""");

    ExpectLines(result, [
        "True",
        "False",
        "null",
        "True",
        "[Error] automatic failure",
        "False",
        "1",
        "automatic failure",
        "no stack",
        "True",
        "abc123",
        "abc",
        "123",
        "Core",
        "Core",
        "Browser",
        "PSWasm Browser Host",
        "True",
        "browser:/",
        "browser:/home",
        "browser:/pswasm",
        "PSWasm",
        "True",
        "False",
        "0",
        "0",
        "no debug",
        "2",
        "browser",
        "42",
        "0",
        "1"
    ]);
}

static async ValueTask VerifyPreferenceVariablesAsync()
{
    var result = await ExecuteAsync("""
$ConfirmPreference
$DebugPreference
$ErrorActionPreference
$ErrorView
$FormatEnumerationLimit
$InformationPreference
$ProgressPreference
$VerbosePreference
$WarningPreference
$WhatIfPreference
$PSDefaultParameterValues.Count
$OutputEncoding
Write-Debug 'hidden debug'
Write-Information 'hidden info'
$VerbosePreference = 'Continue'
Write-Verbose 'shown verbose'
$WarningPreference = 'SilentlyContinue'
Write-Warning 'hidden warning'
'after hidden warning'
$WarningPreference = 'Continue'
Write-Warning 'shown warning'
$ErrorActionPreference = 'SilentlyContinue'
Write-Error 'hidden error'
$?
$Error.Count
$ErrorActionPreference = 'Continue'
$values = 1,2,3
"$values"
$OFS = '|'
"$values"
try {
    $ErrorActionPreference = 'Stop'
    Write-Error 'stop error'
    'skipped'
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "High",
        "SilentlyContinue",
        "Continue",
        "ConciseView",
        "4",
        "SilentlyContinue",
        "Continue",
        "SilentlyContinue",
        "Continue",
        "False",
        "0",
        "utf-8",
        "[Verbose] shown verbose",
        "after hidden warning",
        "[Warning] shown warning",
        "False",
        "1",
        "1 2 3",
        "1|2|3",
        "[Error] stop error",
        "The running command stopped because the preference variable \"ErrorActionPreference\" is set to Stop: stop error"
    ]);
}

static async ValueTask VerifyCommonParametersAsync()
{
    var result = await ExecuteAsync("""
Write-Verbose 'hidden verbose'
Write-Verbose 'shown verbose' -Verbose
Write-Debug 'shown debug' -Debug
Write-Verbose 'alias verbose' -vb
Write-Warning 'hidden warning' -WarningAction SilentlyContinue
'after hidden warning'
Write-Information 'shown info' -InformationAction Continue
Write-Progress -Activity 'hidden progress' -ProgressAction SilentlyContinue
Write-Output 'captured output' -OutVariable capturedOutput
$capturedOutput
Write-Output 'appended output' -OutVariable +capturedOutput
$capturedOutput.Count
$capturedOutput[-1]
Write-Warning 'captured warning' -WarningVariable capturedWarning
$capturedWarning
Write-Information 'captured information' -InformationAction Continue -InformationVariable capturedInfo
$capturedInfo
Write-Error 'captured error' -ErrorAction SilentlyContinue -ErrorVariable capturedError
$?
$Error.Count
$capturedError[0].Message
Write-Error 'ignored error' -ErrorAction Ignore
$?
$Error.Count
Write-Output 'pipeline value' -PipelineVariable pipelineValue
$pipelineValue
Write-Output 'buffer accepted' -OutBuffer 10
Write-Output 'whatif accepted' -WhatIf
Write-Output 'confirm accepted' -Confirm
try {
    Write-Error 'stop error' -ErrorAction Stop
    'skipped'
} catch {
    $_.Message
}
""");

    ExpectLines(result, [
        "[Verbose] shown verbose",
        "[Debug] shown debug",
        "[Verbose] alias verbose",
        "after hidden warning",
        "[Information] shown info",
        "captured output",
        "captured output",
        "appended output",
        "2",
        "appended output",
        "[Warning] captured warning",
        "captured warning",
        "[Information] captured information",
        "captured information",
        "False",
        "1",
        "captured error",
        "False",
        "1",
        "pipeline value",
        "pipeline value",
        "buffer accepted",
        "whatif accepted",
        "confirm accepted",
        "[Error] stop error",
        "The running command stopped because the preference variable \"ErrorActionPreference\" is set to Stop: stop error"
    ]);
}

static async ValueTask VerifyRuntimeSessionReuseAsync()
{
    var runtime = new PowerShellWasmRuntime();
    var first = await runtime.ExecuteAsync("""
$value = 1
function Add-One($InputValue) {
    $InputValue + 1
}
$value
""");

    var second = await runtime.ExecuteAsync("""
$value = Add-One $value
$value
""");

    ExpectLines(first, [
        "1"
    ]);
    ExpectLines(second, [
        "2"
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

static string HeaderValue(HttpRequestMessage request, string name) =>
    request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}
