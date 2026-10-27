using PSWasm;

internal static class IndexingVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records were independently recorded with native PowerShell 7.6.1.
        // Invalid indexes use catch markers so coverage does not depend on localized error text.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("index_array_scalar", """
$a=10,20,30;$r=$a[1];"value=$r;type=$($r.GetType().Name);count=$($r.Count)"
""", [new("Output", "value=20;type=Int32;count=1")]),
            ("index_array_empty_slice", """
$a=10,20,30;$r=$a[@()];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_array_single_slice", """
$a=10,20,30;$r=$a[@(1)];"value=$r;type=$($r.GetType().Name);count=$($r.Count)"
""", [new("Output", "value=20;type=Object[];count=1")]),
            ("index_array_multiple_slice", """
$a=10,20,30;$r=$a[2,0,2];"value=$($r -join ',');type=$($r.GetType().Name);count=$($r.Count)"
""", [new("Output", "value=30,10,30;type=Object[];count=3")]),
            ("index_array_missing_slice", """
$a=10,20,30;$r=$a[4,0,-4,-1,99];"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=10,30;count=2;array=True")]),
            ("index_array_null_slice", """
$a=10,20,30;try{$r=$a[0,$null,1];"value=$($r -join ',');count=$($r.Count)"}catch{'rejected'};'after'
""", [new("Output", "value=10,10,20;count=3"), new("Output", "after")]),
            ("index_array_null_index", """
$a=10,20;try{$a[$null]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_string_scalar", """
$a='abc';$r=$a[1];"value=$r;type=$($r.GetType().Name);int=$([int]$r)"
""", [new("Output", "value=b;type=Char;int=98")]),
            ("index_string_empty_slice", """
$r='abc'[@()];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_string_single_slice", """
$r='abc'[@(1)];"value=$r;type=$($r.GetType().Name);itemType=$($r[0].GetType().Name);count=$($r.Count)"
""", [new("Output", "value=b;type=Object[];itemType=Char;count=1")]),
            ("index_string_multiple_slice", """
$r='abc'[2,0,2];"value=$($r -join ',');type=$($r.GetType().Name);count=$($r.Count);itemType=$($r[0].GetType().Name)"
""", [new("Output", "value=c,a,c;type=Object[];count=3;itemType=Char")]),
            ("index_string_missing", """
$r='abc'[99];"null=$($null -eq $r)";$r='abc'[-4,0,99,-1];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "null=True"), new("Output", "value=,a,,c;count=4")]),
            ("index_string_empty", """
$s='';$r=$s[0];"null=$($null -eq $r)";$r=$s[@(0)];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=True"), new("Output", "null=False;array=True;count=0")]),
            ("index_scalar_scalar", """
$a=42;foreach($i in -2,-1,0,1){$r=$a[$i];"index=$i;value=$r;null=$($null -eq $r)"}
""", [new("Output", "index=-2;value=;null=True"), new("Output", "index=-1;value=42;null=False"), new("Output", "index=0;value=42;null=False"), new("Output", "index=1;value=;null=True")]),
            ("index_scalar_slice", """
$a=42;$r=$a[0,-1,1,0];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count);wrapped=$(@($a[0,-1,1,0]).Count)"
""", [new("Output", "null=True;array=False;count=0;wrapped=0")]),
            ("index_scalar_empty", """
$a=42;$r=$a[@()];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=True;array=False;count=0")]),
            ("index_null_receiver", """
try{$null[0]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_null_receiver_empty", """
try{$r=$null[@()];"null=$($null -eq $r)"}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_dictionary_single_slice", """
$a=@{one=1;two=2};$r=$a[@('one')];"value=$r;type=$($r.GetType().Name);count=$($r.Count)"
""", [new("Output", "value=1;type=Object[];count=1")]),
            ("index_dictionary_empty", """
$a=@{one=1};$r=$a[@()];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_dictionary_missing_slice", """
$a=@{one=1;two=2};$r=$a['none','one',$null,'two'];"value=$($r -join ',');count=$($r.Count);zeroNull=$($null -eq $r[0]);twoNull=$($null -eq $r[2])"
""", [new("Output", "value=,1,,2;count=4;zeroNull=True;twoNull=True")]),
            ("index_nested_array_element", """
$a=@(@(1,2),@(3,4));$r=$a[@(0)];"count=$($r.Count);type=$($r.GetType().Name);firstCount=$($r[0].Count);firstType=$($r[0].GetType().Name)"
""", [new("Output", "count=1;type=Object[];firstCount=2;firstType=Object[]")]),
            ("index_nested_indexes", """
$a=10,20,30;try{$r=$a[@(0),@(1,2)];"value=$($r -join ',');count=$($r.Count)"}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_list_slice", """
$a=[System.Collections.Generic.List[int]]@(10,20,30);$r=$a[@(1)];"value=$r;type=$($r.GetType().Name);count=$($r.Count)";$r=$a[-1,0,99];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=20;type=Object[];count=1"), new("Output", "value=30,10,;count=3")]),
            ("index_iterator_partial", """
$a=(10,20,30).GetEnumerator();[void]$a.MoveNext();$r=$a[0];"type=$($r.GetType().Name);current=$($a.Current)";[void]$r.MoveNext();"shared=$($a.Current)";"rest=$(@($a) -join ',')"
""", [new("Output", "type=ArrayEnumerator;current=10"), new("Output", "shared=20"), new("Output", "rest=30")]),
            ("index_no_output_receiver", """
function None{};try{$r=(None)[0];"null=$($null -eq $r)"}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_null_element", """
$a=1,$null,3;$r=$a[1];"null=$($null -eq $r)";$r=$a[0,1,2];"value=$($r -join ',');count=$($r.Count);oneNull=$($null -eq $r[1])"
""", [new("Output", "null=True"), new("Output", "value=1,,3;count=3;oneNull=True")]),
            ("index_array_empty_source", """
$a=@();$r=$a[0];"null=$($null -eq $r)";$r=$a[0,1];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=True"), new("Output", "null=False;array=True;count=0")]),
            ("index_array_missing_scalar_vs_null", """
$a=10,20;$r=@($a[8]);$s=@($a[-3]);"right=$($r.Count);left=$($s.Count);rightNull=$($null -eq $r[0])"
""", [new("Output", "right=1;left=1;rightNull=True")]),
            ("index_array_all_missing_slice", """
$a=10,20;$r=$a[-3,2,99];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_array_missing_single_slice", """
$a=10,20;$r=$a[@(99)];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_array_ranges", """
$a=10,20,30,40;"desc=$($a[3..0] -join ',');negative=$($a[-1..-4] -join ',');mixed=$($a[-5..1] -join ',')"
""", [new("Output", "desc=40,30,20,10;negative=40,30,20,10;mixed=10,20,30,40,10,20")]),
            ("index_array_typed_indices", """
$a='zero','one','two';[int[]]$i=@(1);$r=$a[$i];"type=$($r.GetType().Name);count=$($r.Count);value=$r";[long[]]$j=2,0;"long=$($a[$j] -join ',')"
""", [new("Output", "type=Object[];count=1;value=one"), new("Output", "long=two,zero")]),
            ("index_array_index_arraylist", """
$a=10,20,30;$i=[System.Collections.ArrayList]@(2,0);$r=$a[$i];"value=$($r -join ',');count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "value=30,10;count=2;type=Object[]")]),
            ("index_array_index_list", """
$a=10,20,30;$i=[System.Collections.Generic.List[int]]@(2,0);$r=$a[$i];"value=$($r -join ',');count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "value=30,10;count=2;type=Object[]")]),
            ("index_array_numeric_conversion", """
$a=10,20,30,40;"float=$($a[1.6]);even=$($a[2.5]);odd=$($a[1.5]);text=$($a['2']);true=$($a[$true]);false=$($a[$false])"
""", [new("Output", "float=30;even=30;odd=30;text=30;true=20;false=10")]),
            ("index_array_empty_string_index", """
$a=10,20;"value=$($a['']);space=$($a['   ']);hex=$($a['0x1'])"
""", [new("Output", "value=10;space=10;hex=20")]),
            ("index_array_bad_string_index", """
$a=10,20,30;try{$r=$a['bad'];"null=$($null -eq $r);count=$(@($a['bad']).Count)"}catch{'rejected'};"slice=$($a[0,'bad',2] -join ',')"
""", [new("Output", "null=True;count=1"), new("Output", "slice=10,30")]),
            ("index_array_overflow_index", """
$a=10,20;try{$r=$a[2147483648];"null=$($null -eq $r)"}catch{'rejected'};try{$r=$a[0,2147483648,1];"slice=$($r -join ',')"}catch{'slice-rejected'};'after'
""", [new("Output", "null=True"), new("Output", "slice=10,20"), new("Output", "after")]),
            ("index_array_nan_index", """
$a=10,20;try{$r=$a[[double]::NaN];"null=$($null -eq $r)"}catch{'rejected'};'after'
""", [new("Output", "null=True"), new("Output", "after")]),
            ("index_array_char_index", """
$a=10,20,30;try{$r=$a[[char]'1'];"null=$($null -eq $r)"}catch{'rejected'};try{$r=$a[[char]1];"value=$r"}catch{'control-rejected'}
""", [new("Output", "null=True"), new("Output", "value=20")]),
            ("index_array_dictionary_index", """
$a=10,20;try{$a[@{x=0}]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_array_bad_index_in_slice", """
$a=10,20;try{$r=$a[0,@{x=1},1];"value=$($r -join ',')"}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_array_unary_null_slice", """
$a=10,20;$r=$a[,$null];"count=$($r.Count);type=$($r.GetType().Name);value=$r"
""", [new("Output", "count=1;type=Object[];value=10")]),
            ("index_array_object_cast_index_array", """
$a=10,20,30;$i=2,0;$r=$a[[object]$i];"value=$($r -join ',');count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "value=30,10;count=2;type=Object[]")]),
            ("index_array_nested_null_elements", """
$a=@(@(),@($null),@(1,2));$r=$a[0,1,2];"count=$($r.Count);first=$($r[0].Count);second=$($r[1].Count);third=$($r[2].Count);null=$($null -eq $r[1][0])"
""", [new("Output", "count=3;first=0;second=1;third=2;null=True")]),
            ("index_array_scalar_nested_identity", """
$inner=1,2;$a=@($inner,@(3,4));$r=$a[0];$r[0]=9;"source=$($a[0] -join ',');count=$($r.Count)"
""", [new("Output", "source=9,2;count=2")]),
            ("index_array_slice_nested_identity", """
$inner=1,2;$a=@($inner,@(3,4));$r=$a[@(0)];$r[0][0]=9;"source=$($a[0] -join ',');outer=$($r.Count);inner=$($r[0].Count)"
""", [new("Output", "source=9,2;outer=1;inner=2")]),
            ("index_array_slice_independent_container", """
$a=10,20;$r=$a[0,1];$r[0]=99;"source=$($a -join ',');slice=$($r -join ',')"
""", [new("Output", "source=10,20;slice=99,20")]),
            ("index_array_slice_nooutput", """
function None{};$a=1,(None),$null,4;"source=$($a.Count)";$r=$a[0,1,2,3];"count=$($r.Count);value=$($r -join ',');secondNull=$($null -eq $r[1])"
""", [new("Output", "source=4"), new("Output", "count=3;value=1,,4;secondNull=True")]),
            ("index_array_nooutput_scalar", """
function None{};$a=1,(None),3;"selected=$(@($a[1]).Count);missing=$(@($a[99]).Count)"
""", [new("Output", "selected=0;missing=1")]),
            ("index_string_negative_range", """
$s='abcd';"last=$($s[-1]);first=$($s[-4]);reverse=$($s[-1..-4] -join '')"
""", [new("Output", "last=d;first=a;reverse=dcba")]),
            ("index_string_null_slice", """
$s='abc';$r=$s[0,$null,-1];"value=$($r -join ',');count=$($r.Count)";try{$s[$null]}catch{'rejected'}
""", [new("Output", "value=a,a,c;count=3"), new("Output", "rejected")]),
            ("index_string_bad_string_index", """
$s='abc';try{$r=$s['bad'];"null=$($null -eq $r)"}catch{'rejected'};try{$r=$s[0,'bad',1];"value=$($r -join ',');count=$($r.Count)"}catch{'slice-rejected'}
""", [new("Output", "null=True"), new("Output", "value=a,,b;count=3")]),
            ("index_string_char_index", """
$s='abc';$r=$s[[char]1];"value=$r;type=$($r.GetType().Name);int=$([int]$r)";"outside=$($null -eq $s[[char]'1'])"
""", [new("Output", "value=b;type=Char;int=98"), new("Output", "outside=True")]),
            ("index_string_empty_multiple_slice", """
$s='';$r=$s[0,1,-1];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count);value=$($r -join ',')"
""", [new("Output", "null=False;array=True;count=3;value=,,")]),
            ("index_string_utf16", """
$s='A😀Z';$r=$s[1,2];"length=$($s.Length);count=$($r.Count);first=$([int]$r[0]);second=$([int]$r[1]);joined=$($r -join '')"
""", [new("Output", "length=4;count=2;first=55357;second=56832;joined=😀")]),
            ("index_string_invalid_type_index", """
$s='abc';try{$s[@{x=0}]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_list_empty_slice", """
$a=[System.Collections.Generic.List[int]]@(10,20);$r=$a[@()];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=False;array=True;count=0")]),
            ("index_list_missing_scalar", """
$a=[System.Collections.Generic.List[int]]@(10,20);$r=@($a[99]);"count=$($r.Count);null=$($null -eq $r[0])"
""", [new("Output", "count=1;null=True")]),
            ("index_list_invalid_string", """
$a=[System.Collections.Generic.List[int]]@(10,20);try{$r=$a['bad'];"null=$($null -eq $r)"}catch{'rejected'};try{$r=$a[0,'bad',1];"value=$($r -join ',');count=$($r.Count)"}catch{'slice-rejected'}
""", [new("Output", "null=True"), new("Output", "value=10,,20;count=3")]),
            ("index_list_null_slice", """
$a=[System.Collections.Generic.List[int]]@(10,20);$r=$a[$null,1];"value=$($r -join ',');count=$($r.Count)";try{$a[$null]}catch{'rejected'}
""", [new("Output", "value=10,20;count=2"), new("Output", "rejected")]),
            ("index_arraylist_slice", """
$a=[System.Collections.ArrayList]@(10,20,30);$r=$a[@(1)];"value=$r;type=$($r.GetType().Name);count=$($r.Count)";$r=$a[-1,99,-4,0];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=20;type=Object[];count=1"), new("Output", "value=30,,,10;count=4")]),
            ("index_arraylist_empty", """
$a=[System.Collections.ArrayList]@();$r=$a[0,1];"count=$($r.Count);array=$($r -is [array]);null=$($null -eq $r)"
""", [new("Output", "count=2;array=True;null=False")]),
            ("index_scalar_single_slice", """
$a=42;$r=$a[@(0)];"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count);wrapped=$(@($a[@(0)]).Count)"
""", [new("Output", "null=True;array=False;count=0;wrapped=0")]),
            ("index_scalar_null_index", """
try{(42)[$null]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_scalar_conversion", """
$a=42;foreach($i in '0','-1','bad',0.2,$false,$true){try{$r=$a[$i];"value=$r;null=$($null -eq $r);wrapped=$(@($a[$i]).Count)"}catch{'rejected'}}
""", [new("Output", "value=42;null=False;wrapped=1"), new("Output", "value=42;null=False;wrapped=1"), new("Output", "value=;null=True;wrapped=0"), new("Output", "value=;null=True;wrapped=0"), new("Output", "value=42;null=False;wrapped=1"), new("Output", "value=;null=True;wrapped=0")]),
            ("index_object_scalar", """
$a=[pscustomobject]@{Name='value'};$r=$a[0];"name=$($r.Name);negative=$($a[-1].Name);missing=$(@($a[1]).Count);slice=$(@($a[0,-1]).Count)"
""", [new("Output", "name=value;negative=value;missing=0;slice=0")]),
            ("index_null_receiver_bad_index", """
try{$null[@{x=0}]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_dictionary_scalar", """
$a=@{one=1;two=$null};"value=$($a['ONE']);missing=$(@($a['none']).Count);null=$(@($a['two']).Count)";try{$a[$null]}catch{'rejected'}
""", [new("Output", "value=1;missing=1;null=1"), new("Output", "rejected")]),
            ("index_dictionary_array_key", """
$key=1,2;$a=@{};$a[$key]='array-key';$r=$a[[object]$key];"objectCount=$($r.Count);objectNull=$($null -eq $r[0])";$r=$a[,$key];"unaryCount=$($r.Count);value=$r"
""", [new("Output", "objectCount=2;objectNull=True"), new("Output", "unaryCount=1;value=array-key")]),
            ("index_dictionary_raw_numeric_key", """
$key=1;$a=@{$key='integer';'1'='string'};$r=$a[1,'1',[long]1];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=integer,string,;count=3")]),
            ("index_dictionary_nested_values", """
$a=@{one=@(1,2);two=@()};$r=$a['one','two','missing'];"count=$($r.Count);first=$($r[0].Count);second=$($r[1].Count);missing=$($null -eq $r[2])"
""", [new("Output", "count=3;first=2;second=0;missing=True")]),
            ("index_ordered_empty_single", """
$a=[ordered]@{x=10;y=20};$r=$a[@()];"empty=$($r.Count);array=$($r -is [array])";$r=$a[@(0)];"count=$($r.Count);array=$($r -is [array]);value=$r"
""", [new("Output", "empty=0;array=True"), new("Output", "count=1;array=True;value=10")]),
            ("index_ordered_mixed_slice", """
$a=[ordered]@{x=10;y=20};$r=$a[-1,0,99,'x',$null];"value=$($r -join ',');count=$($r.Count);missing=$($null -eq $r[2]);null=$($null -eq $r[4])"
""", [new("Output", "value=20,10,,10,;count=5;missing=True;null=True")]),
            ("index_ordered_object_cast_array", """
$a=[ordered]@{0='key';x='second'};$i=0,1;$r=$a[[object]$i];"value=$($r -join ',');count=$($r.Count)";"key=$($a[[object]0])"
""", [new("Output", "value=key,;count=2"), new("Output", "key=key")]),
            ("index_iterator_missing_slice", """
$a=(10,20,30).GetEnumerator();[void]$a.MoveNext();$r=$a[1];"null=$($null -eq $r);current=$($a.Current)";$r=$a[0,-1];"sliceNull=$($null -eq $r);current=$($a.Current)";"rest=$(@($a) -join ',')"
""", [new("Output", "null=True;current=10"), new("Output", "sliceNull=True;current=10"), new("Output", "rest=20,30")]),
            ("index_iterator_negative_scalar", """
$a=(10,20,30).GetEnumerator();[void]$a.MoveNext();$r=$a[-1];"type=$($r.GetType().Name);current=$($r.Current)";[void]$r.MoveNext();"shared=$($a.Current)"
""", [new("Output", "type=ArrayEnumerator;current=10"), new("Output", "shared=20")]),
            ("index_typed_target_slice", """
[int[]]$a=10,20,30;$r=$a[2,0];"type=$($r.GetType().Name);item=$($r[0].GetType().Name);value=$($r -join ',')"
""", [new("Output", "type=Object[];item=Int32;value=30,10")]),
            ("index_index_evaluated_once", """
$script:calls=0;function GetIndex{$script:calls++;0,1};$a=10,20;$r=$a[(GetIndex)];"calls=$script:calls;value=$($r -join ',')"
""", [new("Output", "calls=1;value=10,20")]),
            ("index_string_enumerable_index", """
$s='abc';$i=0,5,2;$r=$s[$i];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($s[$i]).Count)";$r=$s[@(99)];"missingCount=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=a,c;count=2;pipeline=2"), new("Output", "missingCount=0;array=True")]),
            ("index_string_direct_missing_pipeline", """
$s='abc';$r=$s[0,5,2];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($s[0,5,2]).Count);filtered=$(@($r | ForEach-Object {$_}).Count)"
""", [new("Output", "value=a,,c;count=3;pipeline=3;filtered=2")]),
            ("index_list_enumerable_index", """
$a=[System.Collections.Generic.List[int]]@(10,20);$i=0,5,1;$r=$a[$i];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($a[$i]).Count)";$r=$a[@(99)];"missingCount=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=10,20;count=2;pipeline=2"), new("Output", "missingCount=0;array=True")]),
            ("index_list_direct_missing_pipeline", """
$a=[System.Collections.Generic.List[int]]@(10,20);$r=$a[0,5,1];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($a[0,5,1]).Count);filtered=$(@($r | ForEach-Object {$_}).Count)"
""", [new("Output", "value=10,,20;count=3;pipeline=3;filtered=2")]),
            ("index_array_direct_missing_pipeline", """
$a=10,20;$r=$a[0,5,1];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($a[0,5,1]).Count)"
""", [new("Output", "value=10,20;count=2;pipeline=2")]),
            ("index_dictionary_direct_missing_pipeline", """
$a=@{x=1;y=2};$r=$a['x','missing','y'];"value=$($r -join ',');count=$($r.Count);pipeline=$(@($a['x','missing','y']).Count)";$i='x','missing','y';$r=$a[$i];"indirect=$($r.Count);pipeline=$(@($a[$i]).Count)"
""", [new("Output", "value=1,,2;count=3;pipeline=3"), new("Output", "indirect=3;pipeline=3")]),
            ("index_string_indirect_nulls", """
$s='abc';$i=$null,99,0;$r=$s[$i];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,a;count=2")]),
            ("index_arraylist_enumerable_missing", """
$a=[System.Collections.ArrayList]@(10,20);$r=$a[@(0,99,1)];"count=$($r.Count);value=$($r -join ',')"
""", [new("Output", "count=2;value=10,20")]),
            ("index_scalar_wrapped_missing", """
$a=42;"missing=$(@($a[1]).Count);negative=$(@($a[-2]).Count);good=$(@($a[-1]).Count)"
""", [new("Output", "missing=0;negative=0;good=1")]),
            ("index_dictionary_keys_scalar", """
$a=@{x=1;y=2};$keys=$a.Keys;$r=$keys[0];"type=$($r.GetType().Name);count=$($r.Count);missing=$(@($keys[1]).Count);slice=$(@($keys[0,1]).Count)"
""", [new("Output", "type=KeyCollection;count=2;missing=0;slice=0")]),
            ("index_array_numeric_strings", """
$a=10,20,30,40;"hex=$($a['0x2']);binary=$($a['0b10']);decimal=$($a['1.6']);trim=$($a[' 1 ']);negative=$($a['-1']);plus=$($a['+2'])"
""", [new("Output", "hex=30;binary=30;decimal=30;trim=20;negative=40;plus=30")]),
            ("index_string_null_nooutput_index", """
function None{};try{'abc'[(None)]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_array_nooutput_index", """
function None{};$a=10,20;try{$a[(None)]}catch{'rejected'};'after'
""", [new("Output", "rejected"), new("Output", "after")]),
            ("index_array_slice_write_rejected", """
$a=10,20;try{$a[@(0)]=99}catch{'singleton-rejected'};try{$a[0,1]=99}catch{'multiple-rejected'};try{$a[@()]=99}catch{'empty-rejected'};"value=$($a -join ',')"
""", [new("Output", "singleton-rejected"), new("Output", "multiple-rejected"), new("Output", "empty-rejected"), new("Output", "value=10,20")]),
            ("index_list_slice_write_rejected", """
$a=[System.Collections.Generic.List[int]]@(10,20);try{$a[@(0)]=99}catch{'singleton-rejected'};try{$a[0,1]=99}catch{'multiple-rejected'};"value=$($a -join ',')"
""", [new("Output", "singleton-rejected"), new("Output", "multiple-rejected"), new("Output", "value=10,20")]),
            ("index_arraylist_slice_write_rejected", """
$a=[System.Collections.ArrayList]@(10,20);try{$a[@(0)]=99}catch{'singleton-rejected'};try{$a[@()]=99}catch{'empty-rejected'};"value=$($a -join ',')"
""", [new("Output", "singleton-rejected"), new("Output", "empty-rejected"), new("Output", "value=10,20")]),
            ("index_array_null_write_rejected", """
$a=10,20;try{$a[$null]=99}catch{'rejected'};"value=$($a -join ',')"
""", [new("Output", "rejected"), new("Output", "value=10,20")]),
            ("index_list_null_write_rejected", """
$a=[System.Collections.Generic.List[int]]@(10,20);try{$a[$null]=99}catch{'rejected'};"value=$($a -join ',')"
""", [new("Output", "rejected"), new("Output", "value=10,20")]),
            ("index_array_nested_index_write_rejected", """
$a=10,20;try{$a[,@(0)]=99}catch{'rejected'};"value=$($a -join ',')"
""", [new("Output", "rejected"), new("Output", "value=10,20")]),
            ("index_dictionary_array_key_write_preserved", """
$k=@('x');$a=@{};$a[$k]=10;$a[$k]=20;$r=$a[,$k];"count=$($a.Count);slice=$($r.Count);value=$r"
""", [new("Output", "count=1;slice=1;value=20")]),
            ("index_dictionary_null_write_rejected", """
$a=@{x=1};try{$a[$null]=99}catch{'rejected'};"count=$($a.Count);value=$($a['x'])"
""", [new("Output", "rejected"), new("Output", "count=1;value=1")]),
            ("index_array_negative_write", """
$a=10,20,30;$a[-1]=9;"value=$($a -join ',')";try{$a[-4]=99}catch{'rejected'};"after=$($a -join ',')"
""", [new("Output", "value=10,20,9"), new("Output", "rejected"), new("Output", "after=10,20,9")]),
            ("index_array_numeric_index_write", """
$a=10,20,30;$a['0x1']=99;$a[[char]0]=88;"value=$($a -join ',')"
""", [new("Output", "value=88,99,30")]),
            ("index_array_write_rhs_order", """
$script:steps='';$a=10,20;function Rhs{$script:steps+='r';99};function Idx{$script:steps+='i';@(0,1)};try{$a[(Idx)]=(Rhs)}catch{'rejected'};"steps=$script:steps;value=$($a -join ',')"
""", [new("Output", "rejected"), new("Output", "steps=ri;value=10,20")]),
            ("index_ordered_ordinal_sentinel", """
$a=[ordered]@{x=10;y=20};$r=$a[0,99,-1];"count=$($r.Count);value=$($r -join ',');wrapped=$(@($a[0,99,-1]).Count);filtered=$(@($r | ForEach-Object {$_}).Count)";$r=$a[@(0,99,-1)];"indirect=$($r.Count);value=$($r -join ',')"
""", [new("Output", "count=3;value=10,,20;wrapped=3;filtered=2"), new("Output", "indirect=2;value=10,20")]),
            ("index_ordered_key_null", """
$a=[ordered]@{x=10;y=20};$r=$a['x','missing','y'];"count=$($r.Count);filtered=$(@($r | ForEach-Object {$_}).Count)";$r=$a[@('x','missing','y')];"indirect=$($r.Count)"
""", [new("Output", "count=3;filtered=3"), new("Output", "indirect=3")]),
            ("index_ordered_per_index_object_constraint", """
$a=[ordered]@{x='first';0='zero-key';y='third'};$r=$a[[object]0,0,1];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=first,first,zero-key;count=3")]),
            ("index_ordered_typed_index_array", """
$a=[ordered]@{x='first';0='zero-key';y='third'};[int[]]$i=0,1;$r=$a[$i];"value=$($r -join ',');count=$($r.Count)";[object[]]$j=0,1;$r=$a[$j];"objectArray=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=first,zero-key;count=2"), new("Output", "objectArray=first,zero-key;count=2")]),
            ("index_array_indirect_nooutput_index", """
function None{};$a=10,20,30;$i=0,(None),2;$r=$a[$i];"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=10,10,30;count=3")]),
            ("index_string_direct_nooutput_slot", """
$s='abc';$r=$s[0,99,1];"middle=$(@($r[1]).Count);outside=$(@($s[99]).Count);filtered=$(@($r | ForEach-Object {$_}).Count)"
""", [new("Output", "middle=0;outside=1;filtered=2")]),
            ("index_array_empty_invalid_index", """
$a=@();try{$r=$a[@{x=0}];"null=$($null -eq $r)"}catch{'rejected'};try{$a[$null]}catch{'null-rejected'}
""", [new("Output", "rejected"), new("Output", "null-rejected")]),
            ("index_dictionary_direct_multi_write", """
$a=@{x=1;y=2};try{$a['x','y']=99}catch{'rejected'};"count=$($a.Count);x=$($a['x']);y=$($a['y'])"
""", [new("Output", "rejected"), new("Output", "count=2;x=1;y=2")]),
            ("index_dictionary_variable_array_write", """
$key='x','y';$a=@{x=1;y=2};$a[$key]=99;$r=$a[,$key];"count=$($a.Count);x=$($a['x']);y=$($a['y']);array=$r"
""", [new("Output", "count=3;x=1;y=2;array=99")]),
            ("index_ordered_direct_multi_write", """
$a=[ordered]@{x=1;y=2};try{$a[0,1]=99}catch{'rejected'};"count=$($a.Count);x=$($a['x']);y=$($a['y'])"
""", [new("Output", "rejected"), new("Output", "count=2;x=1;y=2")]),
            ("index_char_null", """
try{$r=[char]$null;"type=$($r.GetType().Name);code=$([int]$r)"}catch{'rejected'}
""", [new("Output", "type=Char;code=0")]),
            ("index_char_empty_string", """
try{$r=[char]'';"type=$($r.GetType().Name);code=$([int]$r)"}catch{'rejected'}
""", [new("Output", "rejected")]),
            ("index_char_one_string", """
$r=[char]'A';"type=$($r.GetType().Name);code=$([int]$r);value=$r"
""", [new("Output", "type=Char;code=65;value=A")]),
            ("index_char_many_string", """
try{[char]'AB'}catch{'rejected'}
""", [new("Output", "rejected")]),
            ("index_char_byte", """
$r=[char][byte]65;"type=$($r.GetType().Name);code=$([int]$r);value=$r"
""", [new("Output", "type=Char;code=65;value=A")]),
            ("index_char_int", """
$r=[char][int]65;"type=$($r.GetType().Name);code=$([int]$r);value=$r"
""", [new("Output", "type=Char;code=65;value=A")]),
            ("index_char_long", """
$r=[char][long]65;"type=$($r.GetType().Name);code=$([int]$r);value=$r"
""", [new("Output", "type=Char;code=65;value=A")]),
            ("index_char_double", """
try{$r=[char][double]65;"type=$($r.GetType().Name);code=$([int]$r);value=$r"}catch{'rejected'}
""", [new("Output", "rejected")]),
            ("index_char_decimal", """
try{$r=[char][decimal]65;"type=$($r.GetType().Name);code=$([int]$r);value=$r"}catch{'rejected'}
""", [new("Output", "rejected")]),
            ("index_char_bool", """
try{$r=[char]$true;"true=$([int]$r)"}catch{'true-rejected'};try{$r=[char]$false;"false=$([int]$r)"}catch{'false-rejected'}
""", [new("Output", "true-rejected"), new("Output", "false-rejected")]),
            ("index_char_boundaries", """
foreach($i in -1,0,65535,65536){try{$r=[char]$i;"input=$i;code=$([int]$r)"}catch{"rejected=$i"}}
""", [new("Output", "rejected=-1"), new("Output", "input=0;code=0"), new("Output", "input=65535;code=65535"), new("Output", "rejected=65536")]),
            ("index_char_type_checks", """
$r='abc'[1];"char=$($r -is [char]);system=$($r -is [System.Char]);string=$($r -is [string]);integer=$($r -is [int]);text=$([string]$r)"
""", [new("Output", "char=True;system=True;string=False;integer=False;text=b")]),
            ("index_char_as", """
$a='A' -as [char];$b=65 -as [char];$c='AB' -as [char];$d=$null -as [char];$e=65.0 -as [char];"a=$a;b=$b;cNull=$($null -eq $c);dNull=$($null -eq $d);eNull=$($null -eq $e)"
""", [new("Output", "a=A;b=A;cNull=True;dNull=False;eNull=True")]),
            ("index_char_numeric_casts", """
$r=[char]'A';"int=$([int]$r);long=$([long]$r);double=$([double]$r);decimal=$([decimal]$r);byte=$([byte]$r)"
""", [new("Output", "int=65;long=65;double=65;decimal=65;byte=65")]),
            ("index_char_typed_array", """
[char[]]$r='AB';"type=$($r.GetType().Name);count=$($r.Count);first=$($r[0].GetType().Name);code=$([int]$r[0]);value=$($r -join '')"
""", [new("Output", "type=Char[];count=2;first=Char;code=65;value=AB")]),
            ("index_char_typed_array_identity", """
[char[]]$a='AB';[char[]]$b=$a;$b[0]='Z';"a=$($a -join '');b=$($b -join '');type=$($b.GetType().Name)"
""", [new("Output", "a=ZB;b=ZB;type=Char[]")]),
            ("index_char_typed_array_numeric", """
[char[]]$a=65,66;"value=$($a -join '');first=$([int]$a[0]);type=$($a.GetType().Name)"
""", [new("Output", "value=AB;first=65;type=Char[]")]),
            ("index_char_typed_reassignment", """
[char]$a='A';$a=66;"value=$a;type=$($a.GetType().Name)";try{$a='AB'}catch{'rejected'};"after=$a"
""", [new("Output", "value=B;type=Char"), new("Output", "rejected"), new("Output", "after=B")]),
            ("index_char_utf16_string", """
try{$r=[char]'😀';"code=$([int]$r)"}catch{'rejected'};[char[]]$r='😀';"count=$($r.Count);first=$([int]$r[0]);second=$([int]$r[1]);value=$($r -join '')"
""", [new("Output", "rejected"), new("Output", "count=2;first=55357;second=56832;value=😀")]),
            ("index_char_array_cast", """
try{$r=[char]@('A');"singleton=$r"}catch{'singleton-rejected'};try{$r=[char]@();"empty=$([int]$r)"}catch{'empty-rejected'};try{$r=[char]@('A','B');"multiple=$r"}catch{'multiple-rejected'}
""", [new("Output", "singleton-rejected"), new("Output", "empty-rejected"), new("Output", "multiple-rejected")]),
            ("index_char_typed_array_empty_null", """
[char[]]$a='';[char[]]$b=$null;[char[]]$c=@();"text=$($a.Count);textType=$($a.GetType().Name);null=$($null -eq $b);empty=$($c.Count);emptyType=$($c.GetType().Name)"
""", [new("Output", "text=0;textType=Char[];null=True;empty=0;emptyType=Char[]")]),
            ("index_char_typed_array_elements", """
[char[]]$a=@('A','B');"value=$($a -join '');type=$($a.GetType().Name)";try{[char[]]$b=@('AB')}catch{'rejected'}
""", [new("Output", "value=AB;type=Char[]"), new("Output", "rejected")]),
            ("index_char_bool_conversion", """
$a=[char]0;$b=[char]65;"zero=$([bool]$a);nonzero=$([bool]$b)"
""", [new("Output", "zero=False;nonzero=True")]),
            ("index_customobject_string_index", """
$a=[pscustomobject]@{Name='value'};$r=$a['Name'];"null=$($null -eq $r);output=$(@($a['Name']).Count);property=$($a.Name)";try{$a['Name']='changed'}catch{'rejected'};"after=$($a.Name)"
""", [new("Output", "null=True;output=0;property=value"), new("Output", "rejected"), new("Output", "after=value")]),
            ("index_customobject_enumerator_rejected", """
$a=[pscustomobject]@{Name='value'};try{$a.GetEnumerator()}catch{'rejected'};"property=$($a.Name)"
""", [new("Output", "rejected"), new("Output", "property=value")]),
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
        Console.WriteLine($"Indexing scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
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
