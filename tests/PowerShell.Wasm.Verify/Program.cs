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
    ("expandable strings", VerifyExpandableStringsAsync),
    ("parallel assignment", VerifyParallelAssignmentAsync),
    ("browser-safe dotnet interop", VerifyBrowserSafeDotNetInteropAsync),
    ("variable commands", VerifyVariableCommandsAsync),
    ("command discovery", VerifyCommandDiscoveryAsync),
    ("stream records", VerifyStreamRecordsAsync),
    ("invoke web request", VerifyInvokeWebRequestAsync),
    ("azure auth commands", VerifyAzureAuthCommandsAsync),
    ("pipeline chain operators", VerifyPipelineChainOperatorsAsync),
    ("dom session commands", VerifyDomSessionCommandsAsync),
    ("dom interaction commands", VerifyDomInteractionCommandsAsync),
    ("browser-safe built-ins", VerifyBuiltInsAsync),
    ("splatting and pipeline", VerifySplattingAndPipelineAsync),
    ("object pipeline commands", VerifyObjectPipelineCommandsAsync),
    ("format commands", VerifyFormatCommandsAsync),
    ("json and csv commands", VerifyJsonAndCsvCommandsAsync),
    ("html commands", VerifyHtmlCommandsAsync),
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
$compound = 5
$compound += 3
$compound
($compound *= 2)
$compound -= 4
$compound /= 2
$compound %= 5
$compound
$text = 'a'
$text += 'b'
$text
$list = @(1)
$list += 2
$list.Count
$list[1]
([int]'40') + 2
[string]123
[bool]0
[bool]'text'
([byte[]]@(65,66)).Length
([byte[]]@(65,66))[0]
[string[]]@(1,2) -join ':'
[Convert]::ToBase64String([byte[]]@(65,66))
'abc' -is [string]
42 -is [int]
[int]'42' -is [int]
'42' -is [int]
$null -isnot [string]
'42' -as [int]
('bad' -as [int]) ?? 'null'
([byte[]]@(65,66)) -is [byte[]]
@(1,2) -is [object[]]
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
        "fallback",
        "8",
        "16",
        "1",
        "ab",
        "2",
        "2",
        "42",
        "123",
        "False",
        "True",
        "2",
        "65",
        "1:2",
        "QUI=",
        "True",
        "True",
        "True",
        "False",
        "True",
        "42",
        "null",
        "True",
        "True"
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

        if (path.Equals("/json", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Name":"Ada","Value":7,"Tags":["web","json"]}""", Encoding.UTF8, "application/json")
            };
        }

        if (path.Equals("/text", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("plain text", Encoding.UTF8, "text/plain")
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
$rest = Invoke-RestMethod -Uri 'https://example.test/json' -Headers @{Accept='application/json'}
$rest.Name
$rest.Value
$rest.Tags[1]
irm 'https://example.test/text'
Get-Command irm | Select-Object -ExpandProperty Name
$restSkip = Invoke-RestMethod 'https://example.test/missing' -SkipHttpErrorCheck
$restSkip
try {
    Invoke-RestMethod 'https://example.test/missing'
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
        "Invoke-WebRequest failed with HTTP 404 Not Found.",
        "Ada",
        "7",
        "json",
        "plain text",
        "irm",
        "missing",
        "Invoke-RestMethod failed with HTTP 404 Not Found."
    ]);
}

static async ValueTask VerifyAzureAuthCommandsAsync()
{
    var authHost = new FakeAzureAuthHost();
    var runtime = new PowerShellWasmRuntime(azureAuthHost: authHost);
    var result = await runtime.ExecuteAsync("""
$Context = Connect-AzAccount -ClientId 'client-123' -Tenant 'tenant-abc'
$Context.AuthType
$Context.ContextType
$Context.Account
$Context.TenantId
$Context.ClientId
$Token = Get-AzAccessToken -ResourceUrl 'https://cosmos.azure.com/'
$Token.Token
$Token.ResourceUrl
$Token.Scopes[0]
$Token.AuthType
$ScopedToken = Get-AzAccessToken -Scope 'https://storage.azure.com/user_impersonation'
$ScopedToken.Scopes[0]
$ScopedToken.ResourceUrl -eq ''
$Current = Get-AzContext
$Current.Authenticated
Disconnect-AzAccount
$AfterDisconnect = Get-AzContext
$AfterDisconnect.Authenticated
""");

    ExpectLines(result, [
        "User",
        "Browser",
        "ada@example.test",
        "tenant-abc",
        "client-123",
        "token:https://cosmos.azure.com/user_impersonation",
        "https://cosmos.azure.com/",
        "https://cosmos.azure.com/user_impersonation",
        "User",
        "https://storage.azure.com/user_impersonation",
        "True",
        "True",
        "False"
    ]);

    try
    {
        await runtime.ExecuteAsync("Connect-AzAccount -Identity -ClientId 'client-123'");
        Fail("Expected Connect-AzAccount -Identity to fail in the browser-safe runtime.");
    }
    catch (InvalidOperationException error) when (
        error.Message.Equals("Identity is not supported by browser-safe Azure authentication.", StringComparison.Ordinal))
    {
    }
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
$Map = @{
    '#tenant-id' = 'cosmosTenant'
    '#client-id' = 'cosmosClientId'
}
$Map['#tenant-id']
$Map['#client-id']
$KeyName = '#computed-client-id'
$ComputedMap = @{
    ('#' + 'computed-tenant-id') = 'computedTenant'
    $KeyName = 'computedClient'
}
$ComputedMap['#computed-tenant-id']
$ComputedMap['#computed-client-id']
$obj = [pscustomobject]@{Name='typed'; Value=42}
$obj.Name
$obj['Value']
$psObject = [System.Management.Automation.PSObject]@{Kind='psobject'}
$psObject.Kind
$ordered = [ordered]@{First='one'; Second='two'}
$ordered.First
$ordered.Keys.Count
$Map
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
        "2",
        "cosmosTenant",
        "cosmosClientId",
        "computedTenant",
        "computedClient",
        "typed",
        "42",
        "psobject",
        "one",
        "2",
        "Name        Value",
        "----        -----",
        "#tenant-id  cosmosTenant",
        "#client-id  cosmosClientId"
    ]);
}

static async ValueTask VerifyExpandableStringsAsync()
{
    var result = await ExecuteAsync("""
$Name = 'World'
"Hello $Name ${Name}"
$Token = @{Token='abc123'}
"Bearer $($Token.Token)"
"Trimmed=$((Write-Output ' abc ').Trim())"
$OFS = '|'
"Items=$(Write-Output 'a'; Write-Output 'b')"
""");

    ExpectLines(result, [
        "Hello World World",
        "Bearer abc123",
        "Trimmed=abc",
        "Items=a|b"
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
'abcdef'.Substring(1,3)
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
        "padded",
        "bcd"
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

static async ValueTask VerifyDomInteractionCommandsAsync()
{
    var domHost = new FakeDomHost();
    domHost.Values["#account-name"] = "acct";
    domHost.Values["#database-name"] = "db";

    var runtime = new PowerShellWasmRuntime(domHost: domHost);
    var result = await runtime.ExecuteAsync("""
$dom = New-DomSession -Name Main -Target document
Set-DomText '#status' 'Ready'
Get-DomText '#status'
Set-DomValue '#sample-value' 'typed'
Get-DomValue '#sample-value'
Set-DomProperty '#query-button' Disabled $true
Get-DomProperty '#query-button' Disabled
$account,$database = Get-DomValue '#account-name','#database-name'
$account
$database
Set-DomStorageItem -Storage Local -Key tenantId -Value 'tenant-one'
Get-DomStorageItem -Storage Local -Key tenantId
Remove-DomStorageItem -Storage Local -Key tenantId
Set-DomStorageItem -Storage Local -Key tenantId -Value 'stored-tenant'
$storageBinding = Register-DomStorageBinding -Session $dom -Storage Local -Map @{'#tenant-id'='tenantId'; '#client-id'='clientId'}
$storageBinding.RegistrationType
$storageBinding.Storage
$storageBinding.Event
$storageBinding.Property
Get-DomValue '#tenant-id'
$temporaryStorageBinding = Register-DomStorageBinding -Session $dom -Storage Local -Selector '#temporary' -Key 'temporary'
Unregister-DomStorageBinding $temporaryStorageBinding
$registration = Register-DomEvent -Session $dom -Selector '#query-form' -Event Submit -PreventDefault -ScriptBlock {
    param($Event)
    $AccountName,$DatabaseName = Get-DomValue '#account-name','#database-name'
    Set-DomProperty '#query-button' Disabled $false
    Set-DomText '#status' ($Event.Type + ':' + $AccountName + ':' + $DatabaseName)
}
$registration.RegistrationType
$registration.Selector
$registration.Event
$registration.PreventDefault
$temporaryEvent = Register-DomEvent -Session $dom -Selector '#temporary' -Event Click -ScriptBlock { Set-DomText '#status' 'temporary' }
Unregister-DomEvent $temporaryEvent
$rows = @([pscustomobject]@{Name='Ada';Status='<Ready>'})
$rows | ConvertTo-Html -Fragment -Property Name,Status | Set-DomHtml '#html-output'
Get-Command *-Dom* | Select-Object -ExpandProperty Name
""");

    ExpectLines(result, [
        "Ready",
        "typed",
        "True",
        "acct",
        "db",
        "tenant-one",
        "DomStorageBinding",
        "Local",
        "Input",
        "Value",
        "stored-tenant",
        "DomEvent",
        "#query-form",
        "Submit",
        "True",
        "Clear-DomStorage",
        "Get-DomProperty",
        "Get-DomSession",
        "Get-DomStorageItem",
        "Get-DomText",
        "Get-DomValue",
        "New-DomSession",
        "Register-DomEvent",
        "Register-DomStorageBinding",
        "Remove-DomSession",
        "Remove-DomStorageItem",
        "Set-DomHtml",
        "Set-DomProperty",
        "Set-DomStorageItem",
        "Set-DomText",
        "Set-DomValue",
        "Unregister-DomEvent",
        "Unregister-DomStorageBinding"
    ]);

    var expectedHtml = string.Join(Environment.NewLine, [
        "<table>",
        "<colgroup><col/><col/></colgroup>",
        "<tr><th>Name</th><th>Status</th></tr>",
        "<tr><td>Ada</td><td>&lt;Ready&gt;</td></tr>",
        "</table>"
    ]);
    if (!domHost.Text.TryGetValue("#html-output", out var html) || html != expectedHtml)
    {
        Fail($"Expected Set-DomHtml to receive converted HTML:{Environment.NewLine}{expectedHtml}{Environment.NewLine}Actual:{Environment.NewLine}{html}");
    }

    if (domHost.EventRegistrationCount != 1 || domHost.StorageBindingRegistrationCount != 1)
    {
        Fail($"Expected one active DOM event and storage binding; got events={domHost.EventRegistrationCount}, storage={domHost.StorageBindingRegistrationCount}.");
    }

    domHost.Values["#tenant-id"] = "typed-tenant";
    await domHost.TriggerStorageBindingAsync("#tenant-id", "Input");
    ExpectLines(await runtime.ExecuteAsync("""
Get-DomStorageItem -Storage Local -Key tenantId
Clear-DomStorage -Storage Local
Get-DomStorageItem -Storage Local -Key tenantId
"""), [
        "typed-tenant"
    ]);

    await domHost.TriggerAsync("#query-form", "Submit", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = "Submit"
    });

    ExpectLines(await runtime.ExecuteAsync("""
Get-DomText '#status'
"""), [
        "Submit:acct:db"
    ]);

    if (!domHost.Properties.TryGetValue(("#query-button", "disabled"), out var disabled) || disabled is not bool boolValue || boolValue)
    {
        Fail("Expected Set-DomProperty to set #query-button.disabled to false.");
    }
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
if ((Write-Output 'x') -eq 'x') { 'parenthesized command ok' }
if (('pipe' | Write-Output) -eq 'pipe') { 'parenthesized pipeline ok' }
$Value = (Write-Output ' abc ').Trim()
$Value
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
        "1",
        "parenthesized command ok",
        "parenthesized pipeline ok",
        "abc"
    ]);
}

static async ValueTask VerifyObjectPipelineCommandsAsync()
{
    var result = await ExecuteAsync("""
1..4 | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 10 }
1..4 | ? { $_ -gt 2 } | % { $_ * 10 }
5 % 2
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Where-Object { $PSItem.Value -ge 2 } |
    Select-Object -ExpandProperty Name
@(@{Name='one'; Value=1}, @{Name='two'; Value=2}, @{Name='three'; Value=3}) |
    Select-Object -First 2 Name
$rows = @(
    @{Name='one'; Value=1; Enabled=$false; Tags=@('core')}
    @{Name='two'; Value=2; Enabled=$true; Tags=@('web','api')}
    @{Name='three'; Value=3; Enabled=$true; Tags=@('web')}
)
$rows | Where-Object Value -ge 2 | Select-Object -ExpandProperty Name
$rows | Where-Object -Property Name -EQ 'two' | Select-Object -ExpandProperty Name
$rows | Where-Object Name -like 't*' | Select-Object -ExpandProperty Name
$rows | Where-Object Enabled | Select-Object -ExpandProperty Name
$rows | Where-Object Tags -contains 'api' | Select-Object -ExpandProperty Name
$rows | ForEach-Object Name
@('aa','bbb') | ForEach-Object Length
@(' abc ',' def ') | ForEach-Object Trim
'abcdef' | ForEach-Object Substring 1 3
$rows | Select-Object @{Name='Label';Expression={$_.Name.ToUpperInvariant()}},@{N='Next';E={$_.Value + 1}} | ConvertTo-Json -Compress
$rows | Select-Object @{Name='Copied';Expression='Name'} | ConvertTo-Json -Compress
$rows | Select-Object N* | ConvertTo-Json -Compress
$rows | Select-Object * | Select-Object -First 1 | ConvertTo-Json -Compress
1..3 | ForEach-Object -Begin { 'begin' } -Process { $_ * 2 } -End { 'end' }
$sum = 0
1..3 | ForEach-Object -Begin { $sum = 10 } -Process { $sum += $_ } -End { $sum }
1..2 | ForEach-Object -Begin { 'only-begin' } -End { 'only-end' }
""");

    ExpectLines(result, [
        "30",
        "40",
        "30",
        "40",
        "1",
        "two",
        "three",
        "@{Name=one}",
        "@{Name=two}",
        "two",
        "three",
        "two",
        "two",
        "three",
        "two",
        "three",
        "two",
        "one",
        "two",
        "three",
        "2",
        "3",
        "abc",
        "def",
        "bcd",
        "[{\"Label\":\"ONE\",\"Next\":2},{\"Label\":\"TWO\",\"Next\":3},{\"Label\":\"THREE\",\"Next\":4}]",
        "[{\"Copied\":\"one\"},{\"Copied\":\"two\"},{\"Copied\":\"three\"}]",
        "[{\"Name\":\"one\"},{\"Name\":\"two\"},{\"Name\":\"three\"}]",
        "{\"Name\":\"one\",\"Value\":1,\"Enabled\":false,\"Tags\":[\"core\"]}",
        "begin",
        "2",
        "4",
        "6",
        "end",
        "16",
        "only-begin",
        "only-end"
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

static async ValueTask VerifyHtmlCommandsAsync()
{
    var result = await ExecuteAsync("""
$Rows = @(
    [pscustomobject]@{Id=1; Name='Ada'; Status='<Ready>'}
    [pscustomobject]@{Id=2; Name='Grace'; Status='Done'}
)
$Rows | ConvertTo-Html -Fragment -Property Id,Name,Status
'--doc--'
$Rows | ConvertTo-Html -Title 'Report' -PreContent '<h1>Rows</h1>' -PostContent '<p>Done</p>' -Property Name
""");

    ExpectLines(result, [
        "<table>",
        "<colgroup><col/><col/><col/></colgroup>",
        "<tr><th>Id</th><th>Name</th><th>Status</th></tr>",
        "<tr><td>1</td><td>Ada</td><td>&lt;Ready&gt;</td></tr>",
        "<tr><td>2</td><td>Grace</td><td>Done</td></tr>",
        "</table>",
        "--doc--",
        "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"  \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">",
        "<html xmlns=\"http://www.w3.org/1999/xhtml\">",
        "<head>",
        "<title>Report</title>",
        "</head><body>",
        "<h1>Rows</h1>",
        "<table>",
        "<colgroup><col/></colgroup>",
        "<tr><th>Name</th></tr>",
        "<tr><td>Ada</td></tr>",
        "<tr><td>Grace</td></tr>",
        "</table>",
        "<p>Done</p>",
        "</body></html>"
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
[CmdletBinding()]
[OutputType([string])]
param([string]$ScriptName = 'root')
'script-param:' + $ScriptName
function Add-Prefix($Text) {
    "pre-$Text"
}
Add-Prefix 'one'
Add-Prefix -Text 'two'
function Add-Suffix {
    param([string]$Text = 'default', $Suffix = ($Text + '-suffix'))
    "$Text-suf"
    $Suffix
    $PSBoundParameters.Count
}
Add-Suffix 'one'
Add-Suffix
function Attribute-Param {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Alias('n')][string]$Name,
        [ValidateSet('Dev','Prod')]$Environment = 'Dev'
    )
    "$Name-$Environment"
    $PSBoundParameters.Count
    $PSBoundParameters['Name']
}
Attribute-Param -n 'App'
Attribute-Param 'Svc' 'Prod'
try {
    Attribute-Param 'Bad' 'Test'
} catch {
    'validate-error'
}
function Type-Param {
    param([int]$Count, [switch]$Enabled, [bool]$Flag = 'true')
    'count:' + ($Count + 1)
    'enabled:' + $Enabled
    'flag:' + $Flag
    'count-is-int:' + ($Count -is [int])
    'enabled-is-switch:' + ($Enabled -is [switch])
}
Type-Param -Count '4' -Enabled
Type-Param -Count '2'
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
& { 'call-literal' }
$sb = { 'call-arg:' + $args[0] + ':' + $args.Count }
& $sb 'x'
$sbWithParam = { param([string]$name='default',[string]$suffix='fallback') $name + '-' + $suffix + ':' + $args.Count }
& $sbWithParam 'alpha' 'omega' 'extra'
& $sbWithParam
$metadataScriptBlock = { [CmdletBinding()] param([string]$Text='inline') 'metadata-block:' + $Text }
& $metadataScriptBlock
$prefix = 'scope'
& { $prefix + ':' + ($args -join '|') } 'a' 'b'
1..2 | & {
    'input-count:' + $input.Count
    $input | % { 'input-item:' + $_ }
    'underscore:' + $_
}
1..2 | % { param($item) 'param-item:' + $item }
""");

    ExpectLines(result, [
        "script-param:root",
        "pre-one",
        "pre-two",
        "one-suf",
        "one-suffix",
        "1",
        "default-suf",
        "default-suffix",
        "0",
        "App-Dev",
        "1",
        "App",
        "Svc-Prod",
        "2",
        "Svc",
        "validate-error",
        "count:5",
        "enabled:True",
        "flag:True",
        "count-is-int:True",
        "enabled-is-switch:True",
        "count:3",
        "enabled:False",
        "flag:True",
        "count-is-int:True",
        "enabled-is-switch:True",
        "a,b",
        "pre-three",
        "Add-Prefix",
        "10",
        "20",
        "inner",
        "outer",
        "call-literal",
        "call-arg:x:1",
        "alpha-omega:1",
        "default-fallback:0",
        "metadata-block:inline",
        "scope:a|b",
        "input-count:2",
        "input-item:1",
        "input-item:2",
        "underscore:",
        "param-item:1",
        "param-item:2"
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
1,2,3 | Write-Output -PipelineVariable pipelineItem | ForEach-Object { "pv=$pipelineItem item=$_" }
1,2,3 | Write-Output -PipelineVariable keptItem | Where-Object { $keptItem -gt 1 } | ForEach-Object { "kept=$keptItem item=$_" }
@(
    [pscustomobject]@{Name='Ada';Status='Ready'}
    [pscustomobject]@{Name='Grace';Status='Done'}
) | Write-Output -pv row | Select-Object -ExpandProperty Name | ForEach-Object { "row=$($row.Name) name=$_" }
3,1,2 | Write-Output -PipelineVariable sortedItem | Sort-Object | ForEach-Object { "sorted=$sortedItem item=$_" }
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
        "pv=1 item=1",
        "pv=2 item=2",
        "pv=3 item=3",
        "kept=2 item=2",
        "kept=3 item=3",
        "row=Ada name=Ada",
        "row=Grace name=Grace",
        "sorted=1 item=1",
        "sorted=2 item=2",
        "sorted=3 item=3",
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

sealed class FakeAzureAuthHost : IPowerShellWasmAzureAuthHost
{
    private Dictionary<string, object?> _context = CreateContext(authenticated: false, tenant: string.Empty, clientId: string.Empty);

    public ValueTask<IReadOnlyDictionary<string, object?>> ConnectAsync(
        PowerShellWasmAzureAuthConnectRequest request,
        CancellationToken cancellationToken)
    {
        _context = CreateContext(authenticated: true, request.Tenant, request.ClientId);
        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(_context);
    }

    public ValueTask<IReadOnlyDictionary<string, object?>> GetContextAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(_context);

    public ValueTask<IReadOnlyDictionary<string, object?>> GetAccessTokenAsync(
        PowerShellWasmAzureAuthTokenRequest request,
        CancellationToken cancellationToken)
    {
        var scopes = request.Scopes.ToArray();
        IReadOnlyDictionary<string, object?> token = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Token"] = "token:" + string.Join(",", scopes),
            ["ExpiresOn"] = "2030-01-01T00:00:00Z",
            ["TenantId"] = _context["TenantId"],
            ["UserId"] = _context["UserId"],
            ["Account"] = _context["Account"],
            ["ResourceUrl"] = request.ResourceUrl,
            ["Scopes"] = scopes,
            ["TokenType"] = "Bearer",
            ["AuthType"] = "User",
            ["ContextType"] = "Browser"
        };

        return ValueTask.FromResult(token);
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        _context = CreateContext(authenticated: false, tenant: string.Empty, clientId: string.Empty);
        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, object?> CreateContext(bool authenticated, string tenant, string clientId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Account"] = authenticated ? "ada@example.test" : string.Empty,
            ["UserName"] = authenticated ? "ada@example.test" : string.Empty,
            ["Name"] = authenticated ? "Ada Example" : string.Empty,
            ["TenantId"] = tenant,
            ["UserId"] = authenticated ? "user-123" : string.Empty,
            ["ClientId"] = clientId,
            ["Authenticated"] = authenticated,
            ["AuthType"] = "User",
            ["ContextType"] = "Browser"
        };
}

sealed class FakeDomHost : IPowerShellWasmDomHost
{
    private readonly Dictionary<int, PowerShellWasmDomEventRegistration> _registrations = [];

    public int EventRegistrationCount => _registrations.Count;
    public Dictionary<string, string> Text { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string Selector, string PropertyName), object?> Properties { get; } = [];
    public Dictionary<string, string> LocalStorage { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SessionStorage { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<string> GetTextAsync(string selector, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Text.TryGetValue(selector, out var value) ? value : string.Empty);

    public ValueTask SetTextAsync(string selector, string text, CancellationToken cancellationToken)
    {
        Text[selector] = text;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetHtmlAsync(string selector, string html, CancellationToken cancellationToken)
    {
        Text[selector] = html;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> GetValueAsync(string selector, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Values.TryGetValue(selector, out var value) ? value : string.Empty);

    public ValueTask SetPropertyAsync(string selector, string propertyName, object? value, CancellationToken cancellationToken)
    {
        var normalizedPropertyName = propertyName.ToLowerInvariant();
        if (normalizedPropertyName.Equals("value", StringComparison.OrdinalIgnoreCase))
        {
            Values[selector] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        Properties[(selector, normalizedPropertyName)] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<object?> GetPropertyAsync(string selector, string propertyName, CancellationToken cancellationToken)
    {
        var normalizedPropertyName = propertyName.ToLowerInvariant();
        if (Properties.TryGetValue((selector, normalizedPropertyName), out var value))
        {
            return ValueTask.FromResult(value);
        }

        return ValueTask.FromResult<object?>(normalizedPropertyName.Equals("value", StringComparison.OrdinalIgnoreCase) &&
            Values.TryGetValue(selector, out var text)
                ? text
                : null);
    }

    public ValueTask RegisterEventAsync(PowerShellWasmDomEventRegistration registration, CancellationToken cancellationToken)
    {
        _registrations[registration.Id] = registration;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterEventAsync(int registrationId, CancellationToken cancellationToken)
    {
        _registrations.Remove(registrationId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetStorageItemAsync(string storage, string key, CancellationToken cancellationToken)
    {
        var store = GetStorage(storage);
        return ValueTask.FromResult(store.TryGetValue(key, out var value) ? value : null);
    }

    public ValueTask SetStorageItemAsync(string storage, string key, string value, CancellationToken cancellationToken)
    {
        GetStorage(storage)[key] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveStorageItemAsync(string storage, string key, CancellationToken cancellationToken)
    {
        GetStorage(storage).Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearStorageAsync(string storage, CancellationToken cancellationToken)
    {
        GetStorage(storage).Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask RegisterStorageBindingAsync(PowerShellWasmDomStorageBindingRegistration registration, CancellationToken cancellationToken)
    {
        _storageBindings[registration.Id] = registration;
        var store = GetStorage(registration.Storage);
        foreach (var item in registration.Map)
        {
            if (store.TryGetValue(item.Value, out var value))
            {
                Values[item.Key] = value;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterStorageBindingAsync(int registrationId, CancellationToken cancellationToken)
    {
        _storageBindings.Remove(registrationId);
        return ValueTask.CompletedTask;
    }

    public async ValueTask TriggerAsync(string selector, string eventName, Dictionary<string, object?> eventData)
    {
        var registration = _registrations.Values.Single(item =>
            item.Selector.Equals(selector, StringComparison.OrdinalIgnoreCase) &&
            item.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase));
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventData"] = eventData
        };
        await registration.ScriptBlock.InvokeResultAsync(eventData, variables);
    }

    public ValueTask TriggerStorageBindingAsync(string selector, string eventName)
    {
        foreach (var registration in _storageBindings.Values.Where(item =>
            item.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase) &&
            item.Map.ContainsKey(selector)))
        {
            GetStorage(registration.Storage)[registration.Map[selector]] =
                Values.TryGetValue(selector, out var value) ? value : string.Empty;
        }

        return ValueTask.CompletedTask;
    }

    private readonly Dictionary<int, PowerShellWasmDomStorageBindingRegistration> _storageBindings = [];
    public int StorageBindingRegistrationCount => _storageBindings.Count;

    private Dictionary<string, string> GetStorage(string storage) =>
        storage.Equals("Session", StringComparison.OrdinalIgnoreCase) ? SessionStorage : LocalStorage;
}
