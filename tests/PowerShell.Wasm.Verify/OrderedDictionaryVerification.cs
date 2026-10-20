using System.Collections.Specialized;
using System.Text.RegularExpressions;
using PSWasm;

internal static class OrderedDictionaryVerification
{
    public static async ValueTask VerifyAsync()
    {
        // These script outcomes were verified against PowerShell 7.6.1; plain hashtable enumeration is normalized.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, string? ErrorPattern)[]
        {
            ("ordered literal type identity and aliases", """
$o=[ordered]@{a=1;b=2}
$o.GetType().Name
$o.GetType().FullName
$o -is [ordered]
$o -is [System.Collections.Specialized.OrderedDictionary]
$o -is [System.Collections.IDictionary]
$o -is [hashtable]
[ordered].FullName
""", [new("Output", "OrderedDictionary"), new("Output", "System.Collections.Specialized.OrderedDictionary"), new("Output", "True"), new("Output", "True"), new("Output", "True"), new("Output", "False"), new("Output", "System.Collections.Specialized.OrderedDictionary")], null),
            ("ordered keys values and case-insensitive updates preserve position", """
$o=[ordered]@{z=3;a=1;m=2}
$o.Keys -join ','
$o.Values -join ','
$o.A=9
$o['Z']=8
$o.c=4
$o.Keys -join ','
$o.Values -join ','
""", [new("Output", "z,a,m"), new("Output", "3,1,2"), new("Output", "Z,A,m,c"), new("Output", "8,9,2,4")], null),
            ("ordered integer and string index overloads", """
$key=4
$o=[ordered]@{$key='integer';'4'='string';a='alpha'}
$o.Keys | ForEach-Object { "$($_.GetType().Name):$_" }
$o[0]
$o[1]
$null -eq $o[4]
$o[[object]4]
$o['4']
$o.'4'
$null -eq $o[[long]4]
""", [new("Output", "Int32:4"), new("Output", "String:4"), new("Output", "String:a"), new("Output", "integer"), new("Output", "string"), new("Output", "True"), new("Output", "integer"), new("Output", "string"), new("Output", "string"), new("Output", "True")], null),
            ("ordered negative ordinal reads and missing positions", """
$o=[ordered]@{a=1;b=2;c=3}
$o[-1]
$o[-3]
$null -eq $o[-4]
$null -eq $o[3]
$null -eq $o['missing']
""", [new("Output", "3"), new("Output", "1"), new("Output", "True"), new("Output", "True"), new("Output", "True")], null),
            ("ordered key and ordinal slices retain missing null slots", """
$o=[ordered]@{a=1;b=2}
$byPosition=@($o[0,5,-1])
"count=$($byPosition.Count)|first=$($byPosition[0])|missing=$($null -eq $byPosition[1])|last=$($byPosition[2])"
$byKey=@($o['b','missing','a'])
"count=$($byKey.Count)|first=$($byKey[0])|missing=$($null -eq $byKey[1])|last=$($byKey[2])"
""", [new("Output", "count=3|first=1|missing=True|last=2"), new("Output", "count=3|first=2|missing=True|last=1")], null),
            ("ordered ordinal and object-key writes select distinct targets", """
$o=[ordered]@{a='first';b='second'}
$o[0]='changed'
$o[[object]0]='integer-key'
$o[[long]0]='long-key'
$o.Keys | ForEach-Object { "$($_.GetType().Name):$_" }
$o[0]
$o[[object]0]
$o[[long]0]
""", [new("Output", "String:a"), new("Output", "String:b"), new("Output", "Int32:0"), new("Output", "Int64:0"), new("Output", "changed"), new("Output", "integer-key"), new("Output", "long-key")], null),
            ("ordered invalid ordinal writes preserve values", """
$o=[ordered]@{a=1;b=2}
try {$o[-1]=9} catch {'negative-write'}
try {$o[2]=9} catch {'past-end-write'}
try {$o[$null]=9} catch {'null-write'}
$o.Keys -join ','
$o.Values -join ','
""", [new("Output", "negative-write"), new("Output", "past-end-write"), new("Output", "null-write"), new("Output", "a,b"), new("Output", "1,2")], null),
            ("ordered scalar null read fails without confusing a missing key", """
$o=[ordered]@{a=1}
try {$o[$null]} catch {'null-index'}
$null -eq $o['missing']
$o.a
""", [new("Output", "null-index"), new("Output", "True"), new("Output", "1")], null),
            ("ordered dictionary mutation methods maintain insertion order", """
$o=[ordered]@{a=1;b=2}
$o.Contains('A')
$o.Add('c',3)
$o.Insert(1,'m',7)
$o.Remove('b')
$o.RemoveAt(0)
$o.Keys -join ','
$o.Values -join ','
$o.Clear()
$o.Count
""", [new("Output", "True"), new("Output", "m,c"), new("Output", "7,3"), new("Output", "0")], null),
            ("ordered methods use key overloads and reject duplicates", """
$o=[ordered]@{}
$o.Add(4,'number')
$o.Add('4','text')
$o.Add('a','alpha')
$o.Contains(4)
$o.Contains('4')
try {$o.Add('A','duplicate')} catch {'duplicate-add'}
try {$o.Insert(99,'outside',9)} catch {'invalid-insert'}
$o.Remove(4)
$o.Keys -join ','
$o.Values -join ','
""", [new("Output", "True"), new("Output", "True"), new("Output", "duplicate-add"), new("Output", "invalid-insert"), new("Output", "4,a"), new("Output", "text,alpha")], null),
            ("ordered exposes Contains but not ContainsKey", """
$o=[ordered]@{a=1}
try {$o.ContainsKey('a')} catch {'containskey-unavailable'}
$o.Contains('A')
$o.IsReadOnly
$o.IsFixedSize
$o.IsSynchronized
$o.Count
""", [new("Output", "containskey-unavailable"), new("Output", "True"), new("Output", "False"), new("Output", "False"), new("Output", "False"), new("Output", "1")], null),
            ("ordered constructor retains its case-sensitive comparer", """
$o=[System.Collections.Specialized.OrderedDictionary]::new()
$o.Add('a',1)
$o.Add('A',2)
$o.Count
$o['a']
$o['A']
$capacity=[ordered]::new(3)
$capacity.Add('z',9)
$capacity[0]
""", [new("Output", "2"), new("Output", "1"), new("Output", "2"), new("Output", "9")], null),
            ("ordered collection members can be shadowed by keys", """
$o=[ordered]@{Count=99;Keys='K';Values='V'}
$o.Count
$o.Keys
$o.Values
$o[0]
""", [new("Output", "99"), new("Output", "K"), new("Output", "V"), new("Output", "99")], null),
            ("ordered dictionary stays scalar until explicitly enumerated", """
$o=[ordered]@{b=2;a=1}
@($o).Count
@($o | ForEach-Object {$_}).Count
$o.GetEnumerator() | ForEach-Object {"$($_.Key)=$($_.Value)"}
$o -join ','
[string]$o
""", [new("Output", "1"), new("Output", "1"), new("Output", "b=2"), new("Output", "a=1"), new("Output", "System.Collections.Specialized.OrderedDictionary"), new("Output", "System.Collections.Specialized.OrderedDictionary")], null),
            ("ordered dictionary casts preserve or copy the correct identity", """
$o=[ordered]@{a=1;child=@{n=2}}
$same=[System.Collections.Specialized.OrderedDictionary]$o
$same.a=8
$o.a
$copy=[hashtable]$o
$copy.GetType().FullName
$copy.a=9
$o.a
$copy.child.n=7
$o.child.n
$null -eq ([System.Collections.Specialized.OrderedDictionary]$null)
""", [new("Output", "8"), new("Output", "System.Collections.Hashtable"), new("Output", "8"), new("Output", "7"), new("Output", "True")], null),
            ("ordered as conversion preserves identity and rejects incompatible maps", """
$o=[ordered]@{a=1}
$alias=$o -as [ordered]
$alias.a=4
$o.a
$null -eq (@{a=1} -as [ordered])
try {$bad=[System.Collections.Specialized.OrderedDictionary]@{a=1}} catch {'incompatible-cast'}
""", [new("Output", "4"), new("Output", "True"), new("Output", "incompatible-cast")], null),
            ("ordered concatenation takes the left dictionary type and preserves order", """
$left=[ordered]@{z=3;a=1}
$joined=$left+([ordered]@{m=2;b=4})
$joined.GetType().FullName
$joined.Keys -join ','
$joined.Values -join ','
$joined.z=9
$left.z
$plain=@{x=5}+([ordered]@{y=6})
$plain.GetType().FullName
$plain.Count
$plain.Keys | Sort-Object
""", [new("Output", "System.Collections.Specialized.OrderedDictionary"), new("Output", "z,a,m,b"), new("Output", "3,1,2,4"), new("Output", "3"), new("Output", "System.Collections.Hashtable"), new("Output", "2"), new("Output", "x"), new("Output", "y")], null),
            ("dictionary concatenation rejects overlapping case-insensitive keys", """
$left=[ordered]@{a=1}
try {$sum=$left+@{A=2}} catch {'ordered-overlap'}
try {$sum=@{a=1}+([ordered]@{A=2})} catch {'plain-overlap'}
$left.a
""", [new("Output", "ordered-overlap"), new("Output", "plain-overlap"), new("Output", "1")], null),
            ("ordered concatenation retains distinct raw numeric and string keys", """
$key=4
$left=[ordered]@{$key='number'}
$right=[ordered]@{'4'='text'}
$sum=$left+$right
$sum.Keys | ForEach-Object {"$($_.GetType().Name):$_"}
$sum[[object]4]
$sum['4']
""", [new("Output", "Int32:4"), new("Output", "String:4"), new("Output", "number"), new("Output", "text")], null),
            ("plain hashtable preserves raw numeric and string keys", """
$key=4
$h=@{$key='number';'4'='text'}
$h.GetType().FullName
$h.Count
$h[4]
$h['4']
$h.'4'
$h.Keys | ForEach-Object {"$($_.GetType().Name):$_"} | Sort-Object
""", [new("Output", "System.Collections.Hashtable"), new("Output", "2"), new("Output", "number"), new("Output", "text"), new("Output", "text"), new("Output", "Int32:4"), new("Output", "String:4")], null),
            ("plain numeric key identity is retained across different numeric types", """
$integer=1
$long=[long]1
$h=@{$integer='int';$long='long'}
$h.Count
$h[1]
$h[[long]1]
$h.Keys | ForEach-Object {$_.GetType().Name} | Sort-Object
""", [new("Output", "2"), new("Output", "int"), new("Output", "long"), new("Output", "Int32"), new("Output", "Int64")], null),
            ("plain hashtable slices retain missing null slots", """
$key=4
$h=@{a=1;$key='number'}
$v=@($h['a','missing',4])
"count=$($v.Count)|first=$($v[0])|missing=$($null -eq $v[1])|last=$($v[2])"
""", [new("Output", "count=3|first=1|missing=True|last=number")], null),
            ("plain hashtable updates preserve original key casing", """
$h=@{a=1}
$h.A=2
$h['A']=3
$h.Keys -join ','
$h.a
$h[-1]='negative'
$h[-1]
$h.Count
""", [new("Output", "a"), new("Output", "3"), new("Output", "negative"), new("Output", "2")], null),
            ("plain hashtable methods retain raw keys and comparer", """
$h=@{}
$h.Add(4,'number')
$h.Add('4','text')
$h.Add('a','alpha')
$h.Contains(4)
$h.ContainsKey('4')
$h.ContainsValue('alpha')
try {$h.Add('A','duplicate')} catch {'duplicate-add'}
$copy=$h.Clone()
$copy.Remove(4)
$h.ContainsKey(4)
$copy.ContainsKey(4)
$copy.GetEnumerator() | ForEach-Object {"$($_.Key)=$($_.Value)"} | Sort-Object
$copy.Clear()
$copy.Count
""", [new("Output", "True"), new("Output", "True"), new("Output", "True"), new("Output", "duplicate-add"), new("Output", "True"), new("Output", "False"), new("Output", "4=text"), new("Output", "a=alpha"), new("Output", "0")], null),
            ("runtime duplicate ordered key evaluates the value before rejecting it", """
$state=@{Log=''}
$key='A'
$result='old'
try {$result=[ordered]@{a=$($state.Log+='1';1);$key=$($state.Log+='2';2)}} catch {'duplicate'}
"log=$($state.Log)|result=$result"
""", [new("Output", "duplicate"), new("Output", "log=12|result=old")], null),
            ("runtime duplicate plain key evaluates the value before rejecting it", """
$state=@{Log=''}
$key=4
try {$h=@{4=$($state.Log+='1';1);$key=$($state.Log+='2';2)}} catch {'duplicate'}
$state.Log
""", [new("Output", "duplicate"), new("Output", "12")], null),
            ("null literal keys evaluate their values before failing", """
$state=@{Log=''}
try {$h=[ordered]@{$null=$($state.Log+='O';1)}} catch {'ordered-null'}
try {$h=@{$null=$($state.Log+='H';1)}} catch {'plain-null'}
$state.Log
""", [new("Output", "ordered-null"), new("Output", "plain-null"), new("Output", "OH")], null),
            ("computed key and value expressions are evaluated in source order once", """
$state=@{Log='';N=0}
$o=[ordered]@{($state.N++)=$($state.Log+='A';10);($state.N++)=$($state.Log+='B';20)}
"n=$($state.N)|log=$($state.Log)"
$o.Keys -join ','
$o.Values -join ','
""", [new("Output", "n=2|log=AB"), new("Output", "0,1"), new("Output", "10,20")], null),
            ("parenthesized numeric constant key remains distinct from a string key", """
$o=[ordered]@{(4)='number';'4'='text'}
$o.Count
$o[[object]4]
$o['4']
""", [new("Output", "2"), new("Output", "number"), new("Output", "text")], null),
            ("ordered splatting binds function parameters by name", """
function Join-Values($Left,$Right) {"$Left/$Right"}
$argsByName=[ordered]@{Right='R';Left='L'}
Join-Values @argsByName
$commandArgs=[ordered]@{InputObject=@('a','b');NoEnumerate=$true}
Write-Output @commandArgs | ForEach-Object {"count=$($_.Count)|values=$($_ -join ',')"}
""", [new("Output", "L/R"), new("Output", "count=2|values=a,b")], null),
            ("ordered objects survive selection and property projections", """
$o=[ordered]@{z=3;a=1}
$o | Select-Object -ExpandProperty Count
$o | Where-Object {$_.z -eq 3} | ForEach-Object {$_.GetType().FullName}
@($o | Select-Object -First 1).Count
""", [new("Output", "2"), new("Output", "System.Collections.Specialized.OrderedDictionary"), new("Output", "1")], null),
            ("ordered dictionary JSON follows insertion order and nests maps", """
$o=[ordered]@{z=3;a=[ordered]@{y=2;x=1}}
$o | ConvertTo-Json -Compress
$decoded=$o | ConvertTo-Json -Compress | ConvertFrom-Json
$decoded.a.x
""", [new("Output", "{\"z\":3,\"a\":{\"y\":2,\"x\":1}}"), new("Output", "1")], null),
            ("custom objects preserve an ordered literal's property order", """
$obj=[pscustomobject][ordered]@{z=3;a=1}
$obj.z
$obj.a
$obj | ConvertTo-Json -Compress
""", [new("Output", "3"), new("Output", "1"), new("Output", "{\"z\":3,\"a\":1}")], null),
            ("raw dictionary keys survive variable commands and function output", """
$key=4
$source=@{$key='number';'4'='text'}
Set-Variable -Name retained -Value $source
$copy=Get-Variable retained -ValueOnly
$copy[4]
$copy['4']
function Get-Map {return $source}
$r=Get-Map
$r[4]
$r.Count
""", [new("Output", "number"), new("Output", "text"), new("Output", "number"), new("Output", "2")], null),
            ("ordered compound and increment writes evaluate the index once", """
$o=[ordered]@{a=10;b=20}
$i=0
$o[$i++]++
"i=$i|a=$($o.a)|b=$($o.b)"
$key=4
$o[[object]$key]=30
$o[[object]$key]+=2
$o[[object]$key]
""", [new("Output", "i=1|a=11|b=20"), new("Output", "32")], null),
            ("empty ordered literals preserve dictionary identity", """
$o=[ordered]@{}
$o.Count
@($o).Count
$null -eq $o[0]
$null -eq $o[-1]
$o.Add('first',1)
$o[0]
""", [new("Output", "0"), new("Output", "1"), new("Output", "True"), new("Output", "True"), new("Output", "1")], null),
            ("ordered constant duplicate keys fail during parsing", """
[ordered]@{a=1;A=2}
""", [], "duplicate"),
            ("plain constant duplicate keys fail during parsing", """
@{a=1;A=2}
""", [], "duplicate"),
            ("constant numeric and string key spellings duplicate during parsing", """
[ordered]@{4='number';'4'='text'}
""", [], "duplicate"),
            ("ordered cast on a variable is invalid syntax", """
$h=@{a=1};[ordered]$h
""", [], "ordered.*literal|literal.*ordered"),
            ("ordered index constraints apply to the complete slice expression", """
$o=[ordered]@{a='first';0='zero';b='last'}
$o[[object]0]
$o[([object]0)]
$o[([object]0),2] -join ','
$keySlice=@($o[[object](0,2)])
"count=$($keySlice.Count)|first=$($keySlice[0])|lastNull=$($null -eq $keySlice[1])"
$null -eq $o[[long]0]
""", [new("Output", "zero"), new("Output", "zero"), new("Output", "first,last"), new("Output", "count=2|first=zero|lastNull=True"), new("Output", "True")], null),
            ("null elements inside dictionary slices retain their positions", """
$o=[ordered]@{a='first';b='last'}
$v=@($o['a',$null,'b'])
"ordered=$($v.Count)|$($v[0])|$($null -eq $v[1])|$($v[2])"
$h=@{a='first';b='last'}
$v=@($h['a',$null,'b'])
"plain=$($v.Count)|$($v[0])|$($null -eq $v[1])|$($v[2])"
""", [new("Output", "ordered=3|first|True|last"), new("Output", "plain=3|first|True|last")], null),
            ("hashtable conversion retains the native comparer and reference rules", """
$o=[ordered]@{a=1}
$copy=[hashtable]$o
$copy.ContainsKey('a')
$copy.ContainsKey('A')
$copy.Add('A',2)
$copy.Count
$o.Count
$h=@{a=1}
$same=[hashtable]$h
$same.A=7
$h.a
$h.ContainsKey('A')
$empty=[System.Collections.Specialized.OrderedDictionary]@{}
$empty.Count
""", [new("Output", "True"), new("Output", "False"), new("Output", "2"), new("Output", "1"), new("Output", "7"), new("Output", "True"), new("Output", "0")], null),
            ("retained ordered enumerators advance and reset in insertion order", """
$o=[ordered]@{b=2;a=1}
$e=$o.GetEnumerator()
$e.MoveNext()
"$($e.Key)=$($e.Value)"
$e.MoveNext()
"$($e.Entry.Key)=$($e.Current.Value)"
$e.MoveNext()
$e.Reset()
$e.MoveNext()
$e.Key
""", [new("Output", "True"), new("Output", "b=2"), new("Output", "True"), new("Output", "a=1"), new("Output", "False"), new("Output", "True"), new("Output", "b")], null),
            ("foreach treats dictionaries as scalar and enumerators as entries", """
$o=[ordered]@{b=2;a=1}
$count=0
foreach ($item in $o) {$count++;$item.GetType().Name}
"dictionaryCount=$count"
$count=0
foreach ($entry in $o.GetEnumerator()) {$count++;"$($entry.Key)=$($entry.Value)"}
"entryCount=$count"
""", [new("Output", "OrderedDictionary"), new("Output", "dictionaryCount=1"), new("Output", "b=2"), new("Output", "a=1"), new("Output", "entryCount=2")], null),
            ("invalid numeric dictionary JSON conversion reports an error", """
$key=4
$o=[ordered]@{$key='number';'4'='text'}
try {$o | ConvertTo-Json -Compress} catch {'ordered-numeric-json'}
$h=@{$key='number';'4'='text'}
try {$h | ConvertTo-Json -Compress} catch {'plain-numeric-json'}
""", [new("Output", "ordered-numeric-json"), new("Output", "plain-numeric-json")], null),
            ("explicit hashtable literal cast retains native type identity", """
$h=[hashtable]@{a=1}
$h.GetType().FullName
$h -is [hashtable]
$h.a
$h.ContainsKey('A')
""", [new("Output", "System.Collections.Hashtable"), new("Output", "True"), new("Output", "1"), new("Output", "True")], null),
            ("casting an ordered variable to custom object retains property order", """
$ordered=[ordered]@{z=3;a=1}
$obj=[pscustomobject]$ordered
$obj.z
$obj.a
$obj | ConvertTo-Json -Compress
""", [new("Output", "3"), new("Output", "1"), new("Output", "{\"z\":3,\"a\":1}")], null),
            ("array keys retain identity while dictionary reads treat arrays as slices", """
$key=@(1,2)
$h=@{}
$h[$key]='value'
$h.Contains($key)
$h.Contains(@(1,2))
$h.Count
$h.GetEnumerator() | ForEach-Object {$_.Value}
$slice=@($h[$key])
"count=$($slice.Count)|firstNull=$($null -eq $slice[0])|lastNull=$($null -eq $slice[1])"
$h.Remove($key)
$h.Count
""", [new("Output", "True"), new("Output", "False"), new("Output", "1"), new("Output", "value"), new("Output", "count=2|firstNull=True|lastNull=True"), new("Output", "0")], null),
            ("enumerator handles retain position and output consumes the remaining entries", """
$o=[ordered]@{b=2;a=1}
$e=$o.GetEnumerator()
$e.MoveNext()
$e.Key
@($o.GetEnumerator()).Count
$remaining=@($e | ForEach-Object {"$($_.Key)=$($_.Value)"})
"remaining=$($remaining.Count)|value=$($remaining[0])"
@($e).Count
""", [new("Output", "True"), new("Output", "b"), new("Output", "2"), new("Output", "remaining=1|value=a=1"), new("Output", "0")], null),
            ("dictionary slices keep singleton and empty array identity", """
$o=[ordered]@{a=1;b=2}
$single=$o[@('a')]
"singleArray=$($single -is [array])|count=$($single.Count)|value=$($single[0])"
$empty=$o[@()]
"emptyArray=$($empty -is [array])|count=$($empty.Count)|isNull=$($null -eq $empty)"
$missing=$o[@('missing')]
"missingArray=$($missing -is [array])|count=$($missing.Count)|valueNull=$($null -eq $missing[0])"
""", [new("Output", "singleArray=True|count=1|value=1"), new("Output", "emptyArray=True|count=0|isNull=False"), new("Output", "missingArray=True|count=1|valueNull=True")], null),
            ("regex match dictionaries retain numbered integer keys and named string keys", """
'ab12' -match '(?<word>[a-z]+)([0-9]+)'
$Matches[0]
$Matches[1]
$Matches['word']
$Matches.ContainsKey('0')
$Matches.ContainsKey(0)
$Matches.Count
""", [new("Output", "True"), new("Output", "ab12"), new("Output", "12"), new("Output", "ab"), new("Output", "False"), new("Output", "True"), new("Output", "3")], null),
            ("dictionary Count is entry count while Length is scalar cardinality", """
$o=[ordered]@{a=1;b=2}
$h=@{a=1;b=2}
"ordered:count=$($o.Count)|length=$($o.Length)|longNull=$($null -eq $o.LongLength)"
"plain:count=$($h.Count)|length=$($h.Length)|longNull=$($null -eq $h.LongLength)"
$orderedMember=@($o | ForEach-Object Length)
$plainMember=@($h | ForEach-Object Length)
"orderedMember:count=$($orderedMember.Count)|null=$($null -eq $orderedMember[0])"
"plainMember:count=$($plainMember.Count)|null=$($null -eq $plainMember[0])"
""", [new("Output", "ordered:count=2|length=1|longNull=True"), new("Output", "plain:count=2|length=1|longNull=True"), new("Output", "orderedMember:count=1|null=True"), new("Output", "plainMember:count=1|null=True")], null),
            ("dictionary keys shadow scalar and collection member names", """
$o=[ordered]@{Count=99;Length='L';LongLength='LL'}
$h=@{Count=98;Length='H';LongLength='HH'}
"ordered:$($o.Count)|$($o.Length)|$($o.LongLength)"
"plain:$($h.Count)|$($h.Length)|$($h.LongLength)"
$o | ForEach-Object Length
$h | ForEach-Object Length
""", [new("Output", "ordered:99|L|LL"), new("Output", "plain:98|H|HH"), new("Output", "L"), new("Output", "H")], null),
            ("dictionary method arguments convert empty output to null", """
$h=@{}
try {$h.Add($(& {}),1)} catch {'plain-null-add'}
$h.Count
$h.Add('a',$(& {}))
$h.ContainsValue($null)
$h.ContainsValue($(& {}))
$o=[ordered]@{}
try {$o.Add($(& {}),1)} catch {'ordered-null-add'}
try {$o.Insert(0,$(& {}),1)} catch {'ordered-null-insert'}
$o.Count
$o.Add('a',$(& {}))
$o.Insert(0,'b',$(& {}))
$copy=[hashtable]$o
$copy.ContainsValue($null)
$copy.Count
""", [new("Output", "plain-null-add"), new("Output", "0"), new("Output", "True"), new("Output", "True"), new("Output", "ordered-null-add"), new("Output", "ordered-null-insert"), new("Output", "0"), new("Output", "True"), new("Output", "2")], null),
            ("literal and index storage retain empty output while member writes convert it", """
$h=@{a=$(& {})}
"literalNull=$($h.ContainsValue($null))"
$h['a']=$(& {})
"indexNull=$($h.ContainsValue($null))"
$h.a=$(& {})
"memberNull=$($h.ContainsValue($null))"
$o=[ordered]@{a=$(& {})}
$copy=[hashtable]$o
"orderedLiteralNull=$($copy.ContainsValue($null))"
$o['a']=$(& {})
$copy=[hashtable]$o
"orderedIndexNull=$($copy.ContainsValue($null))"
$o.a=$(& {})
$copy=[hashtable]$o
"orderedMemberNull=$($copy.ContainsValue($null))"
""", [new("Output", "literalNull=False"), new("Output", "indexNull=False"), new("Output", "memberNull=True"), new("Output", "orderedLiteralNull=False"), new("Output", "orderedIndexNull=False"), new("Output", "orderedMemberNull=True")], null),
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
        var hostCases = new (string Name, Func<ValueTask> Run)[]
        {
            ("constant duplicates fail before script side effects", VerifyParseFailureAsync),
            ("host dictionary identity and raw keys survive runtime reuse", VerifyHostDictionaryAsync),
            ("host string dictionaries coerce numeric indexes to their key type", VerifyHostStringDictionaryAsync)
        };
        foreach (var (name, run) in hostCases)
        {
            try { await run(); }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        var total = cases.Length + hostCases.Length;
        Console.WriteLine($"Ordered dictionary scenarios: {total - failures.Count}/{total} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static async ValueTask VerifyParseFailureAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        await runtime.ExecuteAsync("$state=@{Count=0}");
        await AssertFailureAsync("ordered duplicate", runtime, "$state.Count++; [ordered]@{a=1;A=2}", "duplicate");
        await AssertFailureAsync("plain duplicate", runtime, "$state.Count++; @{a=1;A=2}", "duplicate");
        AssertRecords("state after parser failures", await runtime.ExecuteAsync("$state.Count"), [new("Output", "0")]);
    }

    private static async ValueTask VerifyHostDictionaryAsync()
    {
        var dictionary = new OrderedDictionary(StringComparer.OrdinalIgnoreCase) { { 4, "number" }, { "4", "text" } };
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Get-HostMap", new DelegatePowerShellWasmCommand(async (command, token) =>
            await command.ExecutionContext.WriteOutputAsync(dictionary, token)));
        AssertRecords("host dictionary", await runtime.ExecuteAsync("""
$map=Get-HostMap
$map.GetType().FullName
$map[[object]4]
$map['4']
$map[0]='changed'
"""),
        [
            new("Output", "System.Collections.Specialized.OrderedDictionary"),
            new("Output", "number"), new("Output", "text")
        ]);
        if (!Equals(dictionary[(object)4], "changed") || !Equals(dictionary["4"], "text"))
        {
            throw new InvalidOperationException("The host dictionary was copied or its typed keys were conflated.");
        }
        AssertRecords("dictionary reused", await runtime.ExecuteAsync("""
$map[[object]4]
$map['4']
$map.Add('next',3)
$map.Keys | ForEach-Object { "$($_.GetType().Name):$_" }
"""),
        [
            new("Output", "changed"), new("Output", "text"), new("Output", "Int32:4"),
            new("Output", "String:4"), new("Output", "String:next")
        ]);
        if (dictionary.Count != 3) throw new InvalidOperationException("The host dictionary reference was lost across invocations.");
    }

    private static async ValueTask VerifyHostStringDictionaryAsync()
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["4"] = "four" };
        var runtime = new PowerShellWasmRuntime();
        runtime.RegisterCommand("Get-HostStringMap", new DelegatePowerShellWasmCommand(async (command, token) =>
            await command.ExecutionContext.WriteOutputAsync(dictionary, token)));
        // Native generic Dictionary[string,object] indexing converts keys; Hashtable indexing retains raw key identity.
        AssertRecords("host string dictionary", await runtime.ExecuteAsync("""
$map=Get-HostStringMap
$map[4]
$map[5]='five'
$map['5']
$values=$map[4,5,6]
"count=$($values.Count)|first=$($values[0])|second=$($values[1])|missing=$($null -eq $values[2])"
$map.Keys | ForEach-Object { "$($_.GetType().Name):$_" } | Sort-Object
"""),
        [
            new("Output", "four"), new("Output", "five"),
            new("Output", "count=3|first=four|second=five|missing=True"),
            new("Output", "String:4"), new("Output", "String:5")
        ]);
        if (dictionary.Count != 2 || !dictionary.TryGetValue("5", out var value) || !Equals(value, "five"))
        {
            throw new InvalidOperationException("Writing a numeric index did not update the original string-keyed dictionary.");
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





