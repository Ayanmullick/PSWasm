using PSWasm;

internal static class CollectionIntrinsicVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records were independently recorded with native PowerShell 7.6.1, except the final runtime safety guard.
        // Catch markers avoid localized error text; observable shapes and callback effects are asserted directly.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("intrinsic_foreach_property", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'});$r=$a.ForEach('Name');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=a,b;count=2;array=False;type=Collection`1")]),
            ("intrinsic_foreach_property_missing_null", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Other='b'},$null,3);$r=$a.ForEach('Name');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=a,,,;count=4;array=False")]),
            ("intrinsic_foreach_numeric_property", """
$r=(1,2).ForEach(99);"count=$($r.Count);null=$($null -eq $r);value=$($r -join ',')"
""", [new("Output", "count=2;null=False;value=,")]),
            ("intrinsic_foreach_dictionary_property", """
$a=@(@{Name='a'},@{Other='b'});$r=$a.ForEach('Name');"value=$($r -join ',');count=$($r.Count);lastNull=$($null -eq $r[-1])"
""", [new("Output", "value=a,;count=2;lastNull=True")]),
            ("intrinsic_foreach_property_array_values", """
$a=@([pscustomobject]@{Value=@(1,2)},[pscustomobject]@{Value=@(3,4)});$r=$a.ForEach('Value');"value=$($r -join ',');count=$($r.Count);firstArray=$($r[0] -is [array])"
""", [new("Output", "value=System.Object[],System.Object[];count=2;firstArray=True")]),
            ("intrinsic_foreach_property_nested_values", """
$a=,[pscustomobject]@{Value=@(@(1,2),@(3,4))};$r=$a.ForEach('Value');"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',')"
""", [new("Output", "count=1;firstArray=True;first=System.Object[],System.Object[]")]),
            ("intrinsic_foreach_property_set", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'});$r=$a.ForEach('Name','changed');"names=$($a.Name -join ',');null=$($null -eq $r);count=$($r.Count);wrapped=$(@($r).Count)"
""", [new("Output", "names=changed,changed;null=False;count=0;wrapped=0")]),
            ("intrinsic_foreach_property_set_partial_failure", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Other='b'},[pscustomobject]@{Name='c'});$r='old';try{$r=$a.ForEach('Name','changed')}catch{'caught'};"first=$($a[0].Name);last=$($a[2].Name);result=$r"
""", [new("Output", "caught"), new("Output", "first=changed;last=c;result=old")]),
            ("intrinsic_foreach_dictionary_set", """
$a=@(@{Name='a'},@{Other='b'});try{$r=$a.ForEach('Name','changed');"count=$($r.Count)"}catch{'caught'};"first=$($a[0].Name);last=$($a[1].Name)"
""", [new("Output", "count=0"), new("Output", "first=changed;last=changed")]),
            ("intrinsic_foreach_method", """
$r=('abc','def').ForEach('Substring',1);"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=bc,ef;count=2;array=False;type=Collection`1")]),
            ("intrinsic_foreach_method_no_args", """
$r=('a','b').ForEach('ToUpperInvariant');"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=A,B;count=2")]),
            ("intrinsic_foreach_method_multi_args", """
$r=('abc','def').ForEach('Substring',1,1);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=b,e;count=2")]),
            ("intrinsic_foreach_type_conversion", """
$r=('1','2').ForEach([int]);"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name);first=$($r[0].GetType().Name)"
""", [new("Output", "value=1,2;count=2;array=False;type=Collection`1;first=Int32")]),
            ("intrinsic_foreach_type_empty", """
$r=@().ForEach([int]);"count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name);null=$($null -eq $r)"
""", [new("Output", "count=0;array=False;type=Collection`1;null=False")]),
            ("intrinsic_foreach_type_singleton", """
$r=(,'7').ForEach([int]);"value=$r;count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=7;count=1;array=False;type=Collection`1")]),
            ("intrinsic_foreach_type_conversion_error", """
$r='old';try{$r=('1','bad','3').ForEach([int])}catch{'caught'};"result=$r"
""", [new("Output", "caught"), new("Output", "result=old")]),
            ("intrinsic_foreach_script_args", """
$r=(1,2).ForEach({param($a,$b)"item=$_;a=$a;b=$b;args=$($args -join ',');input=$(@($input).Count)"},10,20,30);$r
""", [new("Output", "item=1;a=10;b=20;args=30;input=0"), new("Output", "item=2;a=10;b=20;args=30;input=0")]),
            ("intrinsic_foreach_script_scope", """
$n=0;$temp='old';$r=(1,2).ForEach({$n++;$temp=$_;$_*2});"n=$n;temp=$temp;value=$($r -join ',');type=$($r.GetType().Name)"
""", [new("Output", "n=2;temp=2;value=2,4;type=Collection`1")]),
            ("intrinsic_foreach_script_empty", """
$r=@().ForEach({$_});"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name);array=$($r -is [array])"
""", [new("Output", "null=False;count=0;type=Collection`1;array=False")]),
            ("intrinsic_foreach_script_singleton", """
$r=(,7).ForEach({$_});"value=$r;count=$($r.Count);type=$($r.GetType().Name);array=$($r -is [array])"
""", [new("Output", "value=7;count=1;type=Collection`1;array=False")]),
            ("intrinsic_foreach_script_null_result", """
$r=(1,2).ForEach({$null});"null=$($null -eq $r);count=$($r.Count);value=$($r -join ',')"
""", [new("Output", "null=False;count=2;value=,")]),
            ("intrinsic_foreach_script_no_output", """
$r=(1,2).ForEach({});"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "null=False;count=0;type=Collection`1")]),
            ("intrinsic_foreach_script_break", """
$trace='';$r='old';foreach($outer in 1,2){$trace+="o$outer;";$r=(1,2,3).ForEach({$trace+="i$_;";if($_ -eq 2){break};$true});$trace+='after;'};"trace=$trace;result=$r"
""", [new("Output", "trace=o1;i1;i2;;result=old")]),
            ("intrinsic_foreach_script_continue", """
$trace='';$r='old';foreach($outer in 1,2){$trace+="o$outer;";$r=(1,2,3).ForEach({$trace+="i$_;";if($_ -eq 2){continue};$true});$trace+='after;'};"trace=$trace;result=$r"
""", [new("Output", "trace=o1;i1;i2;o2;i1;i2;;result=old")]),
            ("intrinsic_foreach_script_return", """
$r=(1,2,3).ForEach({if($_ -eq 2){return};$_});"value=$($r -join ',');count=$($r.Count)";'after'
""", [new("Output", "value=1,3;count=2"), new("Output", "after")]),
            ("intrinsic_foreach_script_throw", """
$n=0;$r='old';try{$r=(1,2,3).ForEach({$n++;if($_ -eq 2){throw 'stop'};$_})}catch{'caught'};"result=$r;calls=$n;errors=$($Error.Count)"
""", [new("Output", "caught"), new("Output", "result=old;calls=2;errors=1")]),
            ("intrinsic_foreach_null_target", """
$n=0;try{$r=$null.ForEach({$n++;$_});"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"}catch{'caught'};"calls=$n"
""", [new("Output", "null=False;count=0;type=Collection`1"), new("Output", "calls=0")]),
            ("intrinsic_foreach_scalar_target", """
$r=(7).ForEach({$_*2});"value=$r;count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "value=14;count=1;type=Collection`1")]),
            ("intrinsic_foreach_dictionary_target", """
$a=@{Name='a';Other='b'};$r=$a.ForEach({"name=$($_.Name);count=$($_.Count)"});"value=$r;count=$($r.Count)"
""", [new("Output", "value=name=a;count=2;count=1")]),
            ("intrinsic_foreach_iterator_target", """
$a=(1,2).GetEnumerator();$r=$a.ForEach({$_*2});"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext())"
""", [new("Output", "value=2,4;count=2;next=False")]),
            ("intrinsic_where_shape", """
$r=(1,2,3).Where({$_ -gt 1});"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=2,3;count=2;array=False;type=Collection`1")]),
            ("intrinsic_where_empty", """
$r=@().Where({$true});"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "null=False;count=0;type=Collection`1")]),
            ("intrinsic_where_no_matches", """
$r=(1,2).Where({$false});"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "null=False;count=0;type=Collection`1")]),
            ("intrinsic_where_single_match", """
$r=(1,2).Where({$_ -eq 1});"value=$r;count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=1;count=1;array=False;type=Collection`1")]),
            ("intrinsic_where_null_target", """
$n=0;try{$r=$null.Where({$n++;$true});"count=$($r.Count);type=$($r.GetType().Name)"}catch{'caught'};"calls=$n"
""", [new("Output", "count=0;type=Collection`1"), new("Output", "calls=0")]),
            ("intrinsic_where_scalar_target", """
$r=(7).Where({$true});"value=$r;count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "value=7;count=1;type=Collection`1")]),
            ("intrinsic_where_iterator_target", """
$a=(1,2,3).GetEnumerator();$r=$a.Where({$_ -gt 1},'First');"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext());current=$($a.Current)"
""", [new("Output", "value=2;count=1;next=True;current=3")]),
            ("intrinsic_where_throw", """
$n=0;$r='old';try{$r=(1,2,3).Where({$n++;if($_ -eq 2){throw 'stop'};$true})}catch{'caught'};"result=$r;calls=$n;errors=$($Error.Count)"
""", [new("Output", "caught"), new("Output", "result=old;calls=2;errors=1")]),
            ("intrinsic_where_break", """
$trace='';$r='old';foreach($outer in 1,2){$trace+="o$outer;";$r=(1,2,3).Where({$trace+="i$_;";if($_ -eq 2){break};$true});$trace+='after;'};"trace=$trace;result=$r"
""", [new("Output", "trace=o1;i1;i2;;result=old")]),
            ("intrinsic_where_continue", """
$trace='';$r='old';foreach($outer in 1,2){$trace+="o$outer;";$r=(1,2,3).Where({$trace+="i$_;";if($_ -eq 2){continue};$true});$trace+='after;'};"trace=$trace;result=$r"
""", [new("Output", "trace=o1;i1;i2;o2;i1;i2;;result=old")]),
            ("intrinsic_where_predicate_scope", """
$n=0;$temp=0;$r=(1,2,3).Where({$n++;$temp=$_;$_ -gt 1});"n=$n;temp=$temp;value=$($r -join ',')"
""", [new("Output", "n=3;temp=3;value=2,3")]),
            ("intrinsic_where_null_elements", """
$r=@($null,1,$null).Where({$null -eq $_});"count=$($r.Count);null=$($null -eq $r);value=$($r -join ',')"
""", [new("Output", "count=2;null=False;value=,")]),
            ("intrinsic_foreach_method_array_results", """
$a=[System.Collections.ArrayList]@(1,2);$b=[System.Collections.ArrayList]@(3,4);$r=@($a,$b).ForEach('ToArray');"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',');last=$($r[1] -join ',')"
""", [new("Output", "count=2;firstArray=True;first=1,2;last=3,4")]),
            ("intrinsic_foreach_method_void", """
$a=[System.Collections.Generic.List[int]]@(1);$b=[System.Collections.Generic.List[int]]@(2);$r=@($a,$b).ForEach('Clear');"count=$($r.Count);wrapped=$(@($r).Count);firstNull=$($null -eq $r[0]);a=$($a.Count);b=$($b.Count)"
""", [new("Output", "count=2;wrapped=2;firstNull=True;a=0;b=0")]),
            ("intrinsic_foreach_method_null_element", """
$r='old';try{$r=@('abc',$null,'def').ForEach('Substring',1)}catch{'caught'};"result=$r"
""", [new("Output", "caught"), new("Output", "result=old")]),
            ("intrinsic_foreach_method_partial_side_effect", """
$a=[System.Collections.ArrayList]@(1);$r='old';try{$r=@($a,'bad').ForEach('Add',3)}catch{'caught'};"result=$r;value=$($a -join ',')"
""", [new("Output", "caught"), new("Output", "result=old;value=1,3")]),
            ("intrinsic_foreach_hashtable_method_collision", """
$a=@{Name='a';Clear='value'};$r=$a.ForEach('Clear');"value=$r;count=$($r.Count);keys=$($a.Count)"
""", [new("Output", "value=value;count=1;keys=2")]),
            ("intrinsic_foreach_hashtable_intrinsic_count", """
$a=@{Name='a';Other='b'};$r=$a.ForEach('Count');"value=$r;count=$($r.Count);null=$($null -eq $r[0])"
""", [new("Output", "value=;count=1;null=True")]),
            ("intrinsic_foreach_ordered_method", """
$a=[ordered]@{Name='a';Other='b'};$r=$a.ForEach('Clear');"count=$($r.Count);keys=$($a.Count)"
""", [new("Output", "count=1;keys=0")]),
            ("intrinsic_foreach_set_null", """
$a=,[pscustomobject]@{Value=1};$r=$a.ForEach('Value',$null);"null=$($null -eq $a[0].Value);count=$($r.Count)"
""", [new("Output", "null=True;count=0")]),
            ("intrinsic_foreach_set_array", """
$a=@([pscustomobject]@{Value=1},[pscustomobject]@{Value=2});$r=$a.ForEach('Value',@(3,4));"first=$($a[0].Value -join ',');firstCount=$($a[0].Value.Count);second=$($a[1].Value -join ',');resultCount=$($r.Count)"
""", [new("Output", "first=3,4;firstCount=2;second=3,4;resultCount=0")]),
            ("intrinsic_foreach_set_extra_arguments", """
$a=,[pscustomobject]@{Value=1};try{$a.ForEach('Value',2,3)}catch{'caught'};"value=$($a[0].Value)"
""", [new("Output", "value=2 3")]),
            ("intrinsic_foreach_type_string_null", """
$r=@(1,$null,3).ForEach([string]);"value=$($r -join ',');count=$($r.Count);null=$($null -eq $r[1]);type=$($r[1].GetType().Name)"
""", [new("Output", "value=1,,3;count=3;null=False;type=String")]),
            ("intrinsic_foreach_type_array", """
$r=(1,2).ForEach([int[]]);"type=$($r.GetType().Name);array=$($r -is [array]);count=$($r.Count);firstArray=$($r[0] -is [array])"
""", [new("Output", "type=Int32[];array=True;count=2;firstArray=False")]),
            ("intrinsic_foreach_type_list", """
$r=(1,2).ForEach([System.Collections.Generic.List[int]]);"type=$($r.GetType().Name);array=$($r -is [array]);count=$($r.Count);first=$($r[0])"
""", [new("Output", "type=List`1;array=False;count=2;first=1")]),
            ("intrinsic_foreach_type_extra_argument", """
try{(1,2).ForEach([int],7)}catch{'caught'};'after'
""", [new("Output", "1"), new("Output", "2"), new("Output", "after")]),
            ("intrinsic_foreach_null_expression", """
try{$r=(1,2).ForEach($null);"count=$($r.Count)"}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_empty_name", """
try{$r=(1,2).ForEach('');"count=$($r.Count)"}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_no_arguments", """
try{(1,2).ForEach()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_script_args_array", """
$r=(1,2).ForEach({"item=$_;argsCount=$($args.Count);first=$($args[0] -join ',');firstArray=$($args[0] -is [array])"},@(10,20));$r
""", [new("Output", "item=1;argsCount=1;first=10,20;firstArray=True"), new("Output", "item=2;argsCount=1;first=10,20;firstArray=True")]),
            ("intrinsic_foreach_script_params_restore", """
$x='outer';$args='caller';$_='callerItem';$r=(1,2).ForEach({param($x)"item=$_;x=$x"},9);$r;"x=$x;args=$args;item=$_"
""", [new("Output", "item=1;x=9"), new("Output", "item=2;x=9"), new("Output", "x=9;args=caller;item=callerItem")]),
            ("intrinsic_foreach_script_output_shape", """
$r=(1,2).ForEach({,@($_,($_*10))});"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',')"
""", [new("Output", "count=2;firstArray=True;first=1,10")]),
            ("intrinsic_foreach_script_input_enumeration", """
$input='outer';$r=(1,2).ForEach({"item=$_;input=$(@($input) -join ',');again=$(@($input).Count)"});$r;"outer=$input"
""", [new("Output", "item=1;input=outer;again=1"), new("Output", "item=2;input=outer;again=1"), new("Output", "outer=outer")]),
            ("intrinsic_foreach_string_scalar", """
$r='abc'.ForEach({"value=$_;length=$($_.Length)"});"result=$r;count=$($r.Count)"
""", [new("Output", "result=value=abc;length=3;count=1")]),
            ("intrinsic_foreach_nested_targets", """
$r=@(@(1,2),@(3,4)).ForEach({"count=$($_.Count);value=$($_ -join ',')"});$r
""", [new("Output", "count=2;value=1,2"), new("Output", "count=2;value=3,4")]),
            ("intrinsic_where_negative_count", """
$n=0;try{(1,2).Where({$n++;$true},'Default',-1)}catch{'caught'};"calls=$n"
""", [new("Output", "caught"), new("Output", "calls=0")]),
            ("intrinsic_where_invalid_mode", """
$n=0;try{(1,2).Where({$n++;$true},'Bad')}catch{'caught'};"calls=$n"
""", [new("Output", "caught"), new("Output", "calls=0")]),
            ("intrinsic_where_invalid_numeric_mode", """
$n=0;try{(1,2).Where({$n++;$true},99)}catch{'caught'};"calls=$n"
""", [new("Output", "caught"), new("Output", "calls=0")]),
            ("intrinsic_where_extra_arguments", """
$n=0;try{(1,2).Where({$n++;$true},'Default',1,2)}catch{'caught'};"calls=$n"
""", [new("Output", "caught"), new("Output", "calls=0")]),
            ("intrinsic_where_no_arguments", """
try{(1,2).Where()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_string_predicate", """
try{(1,2).Where('Length')}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_null_predicate_default", """
try{$r=(1,2).Where($null);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_dictionary_scalar", """
$r=@{Name='a';Other='b'}.Where({$_.Name -eq 'a'});"count=$($r.Count);name=$($r[0].Name);keys=$($r[0].Count)"
""", [new("Output", "count=1;name=a;keys=2")]),
            ("intrinsic_where_string_scalar", """
$r='abc'.Where({$_.Length -eq 3});"value=$r;count=$($r.Count)"
""", [new("Output", "value=abc;count=1")]),
            ("intrinsic_where_truthiness_multiple", """
$r=(1,2).Where({$false;$false});"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,2;count=2")]),
            ("intrinsic_where_truthiness_null", """
$r=(1,2).Where({$null});"count=$($r.Count)"
""", [new("Output", "count=0")]),
            ("intrinsic_where_result_nested_array", """
$r=@(@(1,2),@(3,4)).Where({$_[0] -eq 1});"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',')"
""", [new("Output", "count=1;firstArray=True;first=1,2")]),
            ("intrinsic_where_empty_return", """
$r=(1,2,3).Where({if($_ -eq 2){return};$true});"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,3;count=2")]),
            ("intrinsic_where_default_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Default',0);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=12345")]),
            ("intrinsic_where_default_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Default',1);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3;count=1;calls=123")]),
            ("intrinsic_where_default_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Default',2);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4;count=2;calls=1234")]),
            ("intrinsic_where_default_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Default',9);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=12345")]),
            ("intrinsic_where_first_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'First',0);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3;count=1;calls=123")]),
            ("intrinsic_where_first_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'First',1);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3;count=1;calls=123")]),
            ("intrinsic_where_first_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'First',2);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4;count=2;calls=1234")]),
            ("intrinsic_where_first_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'First',9);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=12345")]),
            ("intrinsic_where_last_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Last',0);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=5;count=1;calls=12345")]),
            ("intrinsic_where_last_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Last',1);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=5;count=1;calls=12345")]),
            ("intrinsic_where_last_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Last',2);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=4,5;count=2;calls=12345")]),
            ("intrinsic_where_last_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Last',9);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=12345")]),
            ("intrinsic_where_until_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Until',0);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=1,2;count=2;calls=123")]),
            ("intrinsic_where_until_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Until',1);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=1;count=1;calls=1")]),
            ("intrinsic_where_until_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Until',2);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=1,2;count=2;calls=12")]),
            ("intrinsic_where_until_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Until',9);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=1,2;count=2;calls=123")]),
            ("intrinsic_where_skipuntil_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'SkipUntil',0);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=123")]),
            ("intrinsic_where_skipuntil_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'SkipUntil',1);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3;count=1;calls=123")]),
            ("intrinsic_where_skipuntil_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'SkipUntil',2);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4;count=2;calls=123")]),
            ("intrinsic_where_skipuntil_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'SkipUntil',9);"value=$($r -join ',');count=$($r.Count);calls=$calls"
""", [new("Output", "value=3,4,5;count=3;calls=123")]),
            ("intrinsic_where_split_count_0", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Split',0);"first=$($r[0] -join ',');second=$($r[1] -join ',');outerCount=$($r.Count);firstType=$($r[0].GetType().Name);calls=$calls"
""", [new("Output", "first=3,4,5;second=1,2;outerCount=2;firstType=Collection`1;calls=12345")]),
            ("intrinsic_where_split_count_1", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Split',1);"first=$($r[0] -join ',');second=$($r[1] -join ',');outerCount=$($r.Count);firstType=$($r[0].GetType().Name);calls=$calls"
""", [new("Output", "first=3;second=1,2,4,5;outerCount=2;firstType=Collection`1;calls=123")]),
            ("intrinsic_where_split_count_2", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Split',2);"first=$($r[0] -join ',');second=$($r[1] -join ',');outerCount=$($r.Count);firstType=$($r[0].GetType().Name);calls=$calls"
""", [new("Output", "first=3,4;second=1,2,5;outerCount=2;firstType=Collection`1;calls=1234")]),
            ("intrinsic_where_split_count_9", """
$calls='';$r=(1,2,3,4,5).Where({$calls+="$_";$_ -ge 3},'Split',9);"first=$($r[0] -join ',');second=$($r[1] -join ',');outerCount=$($r.Count);firstType=$($r[0].GetType().Name);calls=$calls"
""", [new("Output", "first=3,4,5;second=1,2;outerCount=2;firstType=Collection`1;calls=12345")]),
            ("intrinsic_foreach_type_arraylist", """
try{$r=(1,2).ForEach([System.Collections.ArrayList]);"value=$($r -join ',');type=$($r.GetType().Name)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_foreach_script_bound_parameters", """
$PSBoundParameters=@{outer=3};$r=(1,2).ForEach({param($x)"item=$_;bound=$($PSBoundParameters.x);count=$($PSBoundParameters.Count)"},9);$r;"outer=$($PSBoundParameters.outer);count=$($PSBoundParameters.Count)"
""", [new("Output", "item=1;bound=9;count=1"), new("Output", "item=2;bound=9;count=1"), new("Output", "outer=3;count=1")]),
            ("intrinsic_foreach_script_parameter_defaults", """
$n=0;$x='outer';$r=(1,2).ForEach({param($x=(++$n))"item=$_;x=$x;n=$n"});$r;"x=$x;n=$n"
""", [new("Output", "item=1;x=1;n=1"), new("Output", "item=2;x=2;n=2"), new("Output", "x=2;n=2")]),
            ("intrinsic_foreach_empty_invalid_expression", """
try{@().ForEach($null)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_null_invalid_expression", """
try{$null.ForEach($null)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_empty_property_result", """
$r=@().ForEach('Name');"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "null=False;count=0;type=Collection`1")]),
            ("intrinsic_foreach_missing_property_set_on_null", """
$a=@([pscustomobject]@{Value=1},$null,[pscustomobject]@{Value=3});try{$a.ForEach('Value',2)}catch{'caught'};"first=$($a[0].Value);last=$($a[2].Value)"
""", [new("Output", "caught"), new("Output", "first=2;last=3")]),
            ("intrinsic_foreach_empty_method_expression", """
$r=@().ForEach('NoSuch',1);"null=$($null -eq $r);count=$($r.Count);type=$($r.GetType().Name)"
""", [new("Output", "null=False;count=0;type=Collection`1")]),
            ("intrinsic_where_null_predicate_last_empty", """
$r=@().Where($null,'Last');"null=$($null -eq $r);count=$($r.Count);array=$($r -is [array]);wrapped=$(@($r).Count);firstNull=$($null -eq $r[0])"
""", [new("Output", "null=False;count=1;array=True;wrapped=1;firstNull=True")]),
            ("intrinsic_where_null_predicate_first_padding", """
$r=(1,2).Where($null,'First',4);"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name);lastNull=$($null -eq $r[-1])"
""", [new("Output", "value=1,2,,;count=4;array=True;type=Object[];lastNull=True")]),
            ("intrinsic_where_null_predicate_until_padding", """
$r=(1,2).Where($null,'Until',4);"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=1,2,,;count=4;array=True;type=Object[]")]),
            ("intrinsic_where_null_predicate_split_padding", """
$r=(1,2).Where($null,'Split',4);"first=$($r[0] -join ',');second=$($r[1] -join ',');count=$($r.Count);firstCount=$($r[0].Count);firstType=$($r[0].GetType().Name);secondType=$($r[1].GetType().Name)"
""", [new("Output", "first=1,2,,;second=;count=2;firstCount=4;firstType=Object[];secondType=Object[]")]),
            ("intrinsic_where_null_mode", """
try{(1,2,3).Where({$_ -gt 1},$null)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_empty_invalid_mode", """
try{@().Where({$true},'Bad')}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_float_count", """
$r=(1,2,3,4).Where({$true},'Default',2.5);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,2;count=2")]),
            ("intrinsic_where_string_count", """
$r=(1,2,3).Where({$true},'Default','2');"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,2;count=2")]),
            ("intrinsic_where_invalid_count", """
try{(1,2).Where({$true},'Default','bad')}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_predicate_parameter", """
$x='outer';$r=(1,2).Where({param($x='default')$x -eq 'default'});"value=$($r -join ',');x=$x"
""", [new("Output", "value=1,2;x=default")]),
            ("intrinsic_where_predicate_automatic_restore", """
$_='outer';$args='caller';$input='inherited';$r=(1,2).Where({$_.GetType().Name -eq 'Int32' -and $args.Count -eq 0 -and $input -eq 'inherited'});"value=$($r -join ',');item=$_;args=$args;input=$input"
""", [new("Output", "value=1,2;item=outer;args=caller;input=inherited")]),
            ("intrinsic_where_predicate_return_truth", """
$r=(1,2,3).Where({return ($_ -ge 2)});"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_where_output_suppression", """
$n=0;$r=(1,2).Where({$n++;'marker'});"value=$($r -join ',');calls=$n"
""", [new("Output", "value=1,2;calls=2")]),
            ("intrinsic_foreach_computed_arguments_once", """
$script:t=0;$script:n=0;$i=0;function Get-Target{$script:t++;'abc';'def'};function Get-Name{$script:n++;'ForEach'};$r=(Get-Target).(Get-Name)('Substring',(++$i));"value=$($r -join ',');targets=$script:t;names=$script:n;args=$i"
""", [new("Output", "value=bc,ef;targets=1;names=1;args=1")]),
            ("intrinsic_where_computed_arguments_once", """
$script:t=0;$script:n=0;$i=0;function Get-Target{$script:t++;1;2;3};function Get-Name{$script:n++;'Where'};$r=(Get-Target).(Get-Name)({$true},'First',(++$i));"value=$r;targets=$script:t;names=$script:n;args=$i"
""", [new("Output", "value=1;targets=1;names=1;args=1")]),
            ("intrinsic_where_null_predicate_first", """
$r=(1,2,3).Where($null,'First');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=1;count=1;array=True;type=Object[]")]),
            ("intrinsic_where_null_predicate_last", """
$r=(1,2,3).Where($null,'Last');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=3;count=1;array=True;type=Object[]")]),
            ("intrinsic_where_null_predicate_until", """
$r=(1,2,3).Where($null,'Until');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=1;count=1;array=True;type=Object[]")]),
            ("intrinsic_where_null_predicate_skipuntil", """
$r=(1,2,3).Where($null,'SkipUntil');"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);type=$($r.GetType().Name)"
""", [new("Output", "value=2,3;count=2;array=True;type=Object[]")]),
            ("intrinsic_where_null_predicate_split", """
$r=(1,2,3).Where($null,'Split');"first=$($r[0] -join ',');second=$($r[1] -join ',');outerArray=$($r -is [array]);firstType=$($r[0].GetType().Name);secondType=$($r[1].GetType().Name)"
""", [new("Output", "first=1;second=2,3;outerArray=True;firstType=Object[];secondType=Object[]")]),
            ("intrinsic_where_numeric_mode_0", """
$r=(1,2,3).Where({$_ -ge 2},0);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_where_numeric_mode_1", """
$r=(1,2,3).Where({$_ -ge 2},1);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_numeric_mode_2", """
$r=(1,2,3).Where({$_ -ge 2},2);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=3;count=1")]),
            ("intrinsic_where_numeric_mode_3", """
$r=(1,2,3).Where({$_ -ge 2},3);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_where_numeric_mode_4", """
$r=(1,2,3).Where({$_ -ge 2},4);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1;count=1")]),
            ("intrinsic_where_numeric_mode_5", """
$r=(1,2,3).Where({$_ -ge 2},5);"first=$($r[0] -join ',');second=$($r[1] -join ',')"
""", [new("Output", "first=2,3;second=1")]),
            ("intrinsic_where_mode_prefixfirst", """
try{$r=(1,2,3).Where({$_ -ge 2},'F');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_prefixlast", """
try{$r=(1,2,3).Where({$_ -ge 2},'L');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=3;count=1")]),
            ("intrinsic_where_mode_prefixskip", """
try{$r=(1,2,3).Where({$_ -ge 2},'Sk');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_where_mode_prefixsplit", """
$r=(1,2,3).Where({$_ -ge 2},'Sp');"first=$($r[0] -join ',');second=$($r[1] -join ',')"
""", [new("Output", "first=2,3;second=1")]),
            ("intrinsic_where_mode_prefixambiguous", """
try{$r=(1,2,3).Where({$_ -ge 2},'S');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_mode_numericstring", """
try{$r=(1,2,3).Where({$_ -ge 2},'2');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=3;count=1")]),
            ("intrinsic_where_mode_enumor", """
try{$r=(1,2,3).Where({$_ -ge 2},'First,Last');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_where_mode_bool", """
try{$r=(1,2,3).Where({$_ -ge 2},$true);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_mode_float", """
try{$r=(1,2,3).Where({$_ -ge 2},1.5);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_foreach_member_nested_missing", """
$a=[System.Collections.Generic.List[string]]@('a','bb');$r=(,$a).ForEach('Length');"count=$($r.Count);null=$($null -eq $r[0]);value=$($r -join ',')"
""", [new("Output", "count=1;null=True;value=")]),
            ("intrinsic_foreach_member_collection_count", """
$r=@(@(1,2),@(3)).ForEach('Count');"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2,1;count=2")]),
            ("intrinsic_foreach_property_readonly_set", """
try{('abc','def').ForEach('Length',2)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_type_null_integer", """
$r=@(1,$null,3).ForEach([int]);"value=$($r -join ',');count=$($r.Count);null=$($null -eq $r[1])"
""", [new("Output", "value=1,0,3;count=3;null=False")]),
            ("intrinsic_foreach_type_conversion_partial", """
$n=0;$r='old';try{$r=@('1','bad','3').ForEach([int])}catch{'caught'};"result=$r;errors=$($Error.Count)"
""", [new("Output", "caught"), new("Output", "result=old;errors=1")]),
            ("intrinsic_foreach_script_empty_string_output", """
$r=(1,2).ForEach({''});"count=$($r.Count);firstNull=$($null -eq $r[0]);firstType=$($r[0].GetType().Name)"
""", [new("Output", "count=2;firstNull=False;firstType=String")]),
            ("intrinsic_foreach_script_multiple_output", """
$r=(1,2).ForEach({$_;$_*10});"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,10,2,20;count=4")]),
            ("intrinsic_where_null_predicate_last_short", """
$r=(1,2).Where($null,'Last',4);"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=1,2;count=2;array=True")]),
            ("intrinsic_where_null_predicate_last_empty_many", """
$r=@().Where($null,'Last',2);"count=$($r.Count);array=$($r -is [array]);null=$($null -eq $r)"
""", [new("Output", "count=0;array=True;null=False")]),
            ("intrinsic_where_null_predicate_skip_count", """
$r=(1,2,3,4).Where($null,'SkipUntil',2);"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=3,4;count=2")]),
            ("intrinsic_where_empty_split", """
$r=@().Where({$true},'Split');"count=$($r.Count);array=$($r -is [array]);firstCount=$($r[0].Count);lastCount=$($r[1].Count);firstType=$($r[0].GetType().Name)"
""", [new("Output", "count=2;array=True;firstCount=0;lastCount=0;firstType=Collection`1")]),
            ("intrinsic_where_first_no_matches", """
$n=0;$r=(1,2).Where({$n++;$false},'First');"count=$($r.Count);calls=$n;null=$($null -eq $r)"
""", [new("Output", "count=0;calls=2;null=False")]),
            ("intrinsic_where_until_no_match", """
$n=0;$r=(1,2).Where({$n++;$false},'Until');"value=$($r -join ',');count=$($r.Count);calls=$n"
""", [new("Output", "value=1,2;count=2;calls=2")]),
            ("intrinsic_where_skipuntil_no_match", """
$n=0;$r=(1,2).Where({$n++;$false},'SkipUntil');"count=$($r.Count);calls=$n"
""", [new("Output", "count=0;calls=2")]),
            ("intrinsic_where_split_false_no_limit", """
$n=0;$r=(1,2).Where({$n++;$false},'Split',1);"first=$($r[0].Count);second=$($r[1] -join ',');calls=$n"
""", [new("Output", "first=0;second=1,2;calls=2")]),
            ("intrinsic_where_last_iterator_exhausted", """
$a=(1,2,3).GetEnumerator();$r=$a.Where({$true},'Last',1);"value=$r;next=$($a.MoveNext())"
""", [new("Output", "value=3;next=False")]),
            ("intrinsic_where_mode_long", """
try{$r=(1,2,3).Where({$_ -ge 2},[long]1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_byte", """
try{$r=(1,2,3).Where({$_ -ge 2},[byte]1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_double", """
try{$r=(1,2,3).Where({$_ -ge 2},[double]1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_decimal", """
try{$r=(1,2,3).Where({$_ -ge 2},[decimal]1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_char", """
try{$r=(1,2,3).Where({$_ -ge 2},[char]1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_mode_invalid_or", """
try{$r=(1,2,3).Where({$_ -ge 2},'Until,Last');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_mode_space", """
try{$r=(1,2,3).Where({$_ -ge 2},' First ');"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_where_mode_negative", """
try{$r=(1,2,3).Where({$_ -ge 2},-1);"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_predicate_script_string", """
try{$r=(1,2).Where('$_ -gt 1');"value=$r;count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_where_predicate_integer", """
try{(1,2).Where(1)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_where_predicate_boolean", """
try{(1,2).Where($true)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_foreach_count_members", """
$r=@(1,$null,[pscustomobject]@{Name='a'},@(3,4),[pscustomobject]@{Count=8}).ForEach('Count');"value=$($r -join ',');count=$($r.Count);firstNull=$($null -eq $r[0]);thirdNull=$($null -eq $r[2])"
""", [new("Output", "value=,,,2,8;count=5;firstNull=True;thirdNull=True")]),
            ("intrinsic_foreach_empty_name_empty_target", """
try{$r=@().ForEach('');"count=$($r.Count)"}catch{'caught'};'after'
""", [new("Output", "count=0"), new("Output", "after")]),
            ("intrinsic_foreach_empty_name_null_target", """
try{$r=$null.ForEach('');"count=$($r.Count)"}catch{'caught'};'after'
""", [new("Output", "count=0"), new("Output", "after")]),
            ("intrinsic_foreach_script_new_parameter", """
$r=(1,2).ForEach({param($fresh)"item=$_;value=$fresh"},9);$r;"outsideNull=$($null -eq $fresh)"
""", [new("Output", "item=1;value=9"), new("Output", "item=2;value=9"), new("Output", "outsideNull=True")]),
            ("intrinsic_where_script_new_parameter", """
$r=(1,2).Where({param($fresh=9)$fresh -eq 9});"value=$($r -join ',');outsideNull=$($null -eq $fresh)"
""", [new("Output", "value=1,2;outsideNull=True")]),
            ("intrinsic_review_ordered_clear_collision", """
$a=[ordered]@{Clear='saved';Name='a'};$r=$a.ForEach('Clear');"count=$($r.Count);firstNull=$($null -eq $r[0]);keys=$($a.Count)"
""", [new("Output", "count=1;firstNull=True;keys=0")]),
            ("intrinsic_review_ordered_count_collision", """
$a=[ordered]@{Count=99;Name='a'};$r=$a.ForEach('Count');"value=$r;count=$($r.Count);key=$($a['Count'])"
""", [new("Output", "value=2;count=1;key=99")]),
            ("intrinsic_review_ordered_name_get_set", """
$a=[ordered]@{Name='a'};$r=$a.ForEach('Name');"readNull=$($null -eq $r[0]);count=$($r.Count)";try{$a.ForEach('Name','b')}catch{'caught'};"name=$($a['Name'])"
""", [new("Output", "readNull=True;count=1"), new("Output", "caught"), new("Output", "name=a")]),
            ("intrinsic_review_list_own_foreach", """
$a=[System.Collections.Generic.List[int]]@(1,2);$script:calls=0;$n=0;$_='outer';$r=$a.ForEach({param($item)$n++;$script:calls++;'ignored'});"null=$($null -eq $r);wrapped=$(@($r).Count);local=$n;calls=$script:calls;item=$_"
""", [new("Output", "null=True;wrapped=0;local=0;calls=2;item=outer")]),
            ("intrinsic_review_typed_collection_methods", """
$r=('1','2').ForEach([int]);$r.Add('3');$r.Insert(1,'4');$yes=$r.Remove('2');$r[0]='5';"value=$($r -join ',');removed=$yes;contains=$($r.Contains('3'));firstType=$($r[0].GetType().Name)"
""", [new("Output", "value=5,4,3;removed=True;contains=True;firstType=Int32")]),
            ("intrinsic_review_typed_collection_reject", """
$r=('1','2').ForEach([int]);try{$r[0]='bad'}catch{'index-caught'};try{$r.Add('bad')}catch{'add-caught'};"value=$($r -join ',')"
""", [new("Output", "index-caught"), new("Output", "add-caught"), new("Output", "value=1,2")]),
            ("intrinsic_review_typed_collection_no_toarray", """
$r=(1,2).ForEach([int]);try{$r.ToArray()}catch{'caught'};"count=$($r.Count)"
""", [new("Output", "caught"), new("Output", "count=2")]),
            ("intrinsic_review_where_iterator_until", """
$a=(1,2,3,4).GetEnumerator();$r=$a.Where({$_ -ge 3},'Until');"value=$($r -join ',');next=$($a.MoveNext());current=$($a.Current)"
""", [new("Output", "value=1,2;next=True;current=4")]),
            ("intrinsic_review_where_iterator_skip", """
$a=(1,2,3,4).GetEnumerator();$r=$a.Where({$_ -ge 2},'SkipUntil',1);"value=$r;next=$($a.MoveNext());current=$($a.Current)"
""", [new("Output", "value=2;next=True;current=3")]),
            ("intrinsic_review_where_iterator_null_first", """
$a=(1,2,3,4).GetEnumerator();$r=$a.Where($null,'First',2);"value=$($r -join ',');next=$($a.MoveNext());current=$($a.Current)"
""", [new("Output", "value=1,2;next=True;current=3")]),
            ("intrinsic_review_where_enum_array", """
foreach($mode in @(@('First','Last'),@(1,2),@('F','L'),@(),@($null))){try{$r=(1,2,3).Where({$_ -ge 2},$mode);"value=$($r -join ',')"}catch{'caught'}}
""", [new("Output", "value=2,3"), new("Output", "value=2,3"), new("Output", "caught"), new("Output", "caught"), new("Output", "caught")]),
            ("intrinsic_review_where_mode_precise_decimal", """
try{$r=(1,2,3).Where({$_ -ge 2},[decimal]'1.000000000000000000001');"value=$r"}catch{'caught'}
""", [new("Output", "value=2")]),
            ("intrinsic_review_list_foreach_argument_binding", """
$a=[System.Collections.Generic.List[int]]@(1,2);$script:trace='';$_='outer';$args='caller';$r=$a.ForEach({param($item)$script:trace+="item=$item;pipeline=$_;args=$($args.Count)|"});"trace=$script:trace;null=$($null -eq $r);outer=$_;args=$args"
""", [new("Output", "trace=item=1;pipeline=outer;args=0|item=2;pipeline=outer;args=0|;null=True;outer=outer;args=caller")]),
            ("intrinsic_review_list_foreach_member_string", """
$a=[System.Collections.Generic.List[string]]@('a','b');try{$a.ForEach('ToUpperInvariant')}catch{'caught'};"value=$($a -join ',')"
""", [new("Output", "caught"), new("Output", "value=a,b")]),
            ("intrinsic_review_list_foreach_empty_invalid", """
$a=[System.Collections.Generic.List[int]]@();try{$a.ForEach('bad')}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_review_typed_string_mutation", """
$r=('a','b').ForEach([string]);$r.Add($null);$r[0]=$null;"value=$($r -join ',');firstNull=$($null -eq $r[0]);lastNull=$($null -eq $r[-1]);count=$($r.Count)"
""", [new("Output", "value=,b,;firstNull=False;lastNull=False;count=3")]),
            ("intrinsic_review_typed_collection_conversion_alias", """
$r=('1','2').ForEach([int]);$alias=$r;$r[0]='3';$r.Add('4');"alias=$($alias -join ',');r=$($r -join ',');type=$($alias.GetType().Name)"
""", [new("Output", "alias=3,2,4;r=3,2,4;type=Collection`1")]),
            ("intrinsic_review_foreach_iterator_conversion_int", """
$a=@('1','bad','3').GetEnumerator();try{$a.ForEach([int])}catch{'caught'};"current=$($a.Current);next=$($a.MoveNext());after=$($a.Current)"
""", [new("Output", "caught"), new("Output", "current=bad;next=True;after=3")]),
            ("intrinsic_review_foreach_iterator_conversion_list", """
$a=@('1','bad','3').GetEnumerator();try{$a.ForEach([System.Collections.Generic.List[int]])}catch{'caught'};"current=$($a.Current);next=$($a.MoveNext());after=$($a.Current)"
""", [new("Output", "caught"), new("Output", "current=bad;next=True;after=3")]),
            ("intrinsic_review_foreach_iterator_conversion_array", """
$a=@('1','bad','3').GetEnumerator();try{$a.ForEach([int[]])}catch{'caught'};"current=$($a.Current);next=$($a.MoveNext());after=$($a.Current)"
""", [new("Output", "caught"), new("Output", "current=;next=False;after=")]),
            ("intrinsic_review_foreach_iterator_conversion_unsupported", """
$a=@('1','bad','3').GetEnumerator();try{$a.ForEach([System.Collections.ArrayList])}catch{'caught'};"current=$($a.Current);next=$($a.MoveNext());after=$($a.Current)"
""", [new("Output", "caught"), new("Output", "current=;next=True;after=1")]),
            ("intrinsic_review_list_foreach_parameter_scope", """
$a=[System.Collections.Generic.List[int]]@(1,2);$x='outer';$n=0;$script:trace='';$a.ForEach({param($x)$n++;$localNew='inside';$script:trace+="x=$x;n=$n;"});"trace=$script:trace;x=$x;n=$n;newNull=$($null -eq $localNew)"
""", [new("Output", "trace=x=1;n=1;x=2;n=1;;x=outer;n=0;newNull=True")]),
            ("intrinsic_review_list_foreach_automatic_scope", """
$a=[System.Collections.Generic.List[int]]@(1,2);$_='outer';$input='inherited';$args='caller';$script:trace='';$a.ForEach({$script:trace+="item=$($args[0]);argc=$($args.Count);pipeline=$_;input=$input|"});"trace=$script:trace;outer=$_;args=$args;input=$input"
""", [new("Output", "trace=item=1;argc=1;pipeline=outer;input=|item=2;argc=1;pipeline=outer;input=|;outer=outer;args=caller;input=inherited")]),
            ("intrinsic_review_list_foreach_return", """
$a=[System.Collections.Generic.List[int]]@(1,2,3);$script:trace='';$r=$a.ForEach({param($item)$script:trace+=$item;if($item -eq 2){return};'ignored'});"trace=$script:trace;null=$($null -eq $r);wrapped=$(@($r).Count)"
""", [new("Output", "trace=123;null=True;wrapped=0")]),
            ("intrinsic_review_list_foreach_throw", """
$a=[System.Collections.Generic.List[int]]@(1,2,3);$script:trace='';$r='old';try{$r=$a.ForEach({param($item)$script:trace+=$item;if($item -eq 2){throw 'stop'};'ignored'})}catch{'caught'};"trace=$script:trace;result=$r;errors=$($Error.Count)"
""", [new("Output", "caught"), new("Output", "trace=12;result=old;errors=2")]),
            ("intrinsic_review_list_foreach_break", """
$a=[System.Collections.Generic.List[int]]@(1,2,3);$script:trace='';foreach($outer in 1,2){$script:trace+="o$outer;";try{$a.ForEach({param($item)$script:trace+="i$item;";if($item -eq 2){break}})}catch{$script:trace+='caught;'};$script:trace+='after;'};"trace=$script:trace"
""", [new("Output", "trace=o1;i1;i2;")]),
            ("intrinsic_review_list_foreach_continue", """
$a=[System.Collections.Generic.List[int]]@(1,2,3);$script:trace='';foreach($outer in 1,2){$script:trace+="o$outer;";try{$a.ForEach({param($item)$script:trace+="i$item;";if($item -eq 2){continue}})}catch{$script:trace+='caught;'};$script:trace+='after;'};"trace=$script:trace"
""", [new("Output", "trace=o1;i1;i2;o2;i1;i2;")]),
            ("intrinsic_review_list_foreach_null_argument", """
$a=[System.Collections.Generic.List[int]]@(1,2);try{$a.ForEach($null)}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("intrinsic_review_list_foreach_extra_arguments", """
$a=[System.Collections.Generic.List[int]]@(1,2);$script:calls=0;try{$a.ForEach({$script:calls++},3)}catch{'caught'};"calls=$script:calls"
""", [new("Output", "caught"), new("Output", "calls=0")]),
            ("intrinsic_review_list_foreach_parameter_default", """
$a=[System.Collections.Generic.List[int]]@(1,2);$_='outer';$script:trace='';$a.ForEach({param($item,$second=$_);$script:trace+="item=$item;second=$second;args=$($args.Count)|"});"trace=$script:trace"
""", [new("Output", "trace=item=1;second=outer;args=0|item=2;second=outer;args=0|")]),
            ("intrinsic_review_list_foreach_mutating_list", """
$a=[System.Collections.Generic.List[int]]@(1,2);$script:calls=0;try{$a.ForEach({param($item)$script:calls++;$a.Add(3)})}catch{'caught'};"value=$($a -join ',');calls=$script:calls"
""", [new("Output", "caught"), new("Output", "value=1,2,3;calls=1")]),
            ("intrinsic_review_where_enum_array_mixed", """
try{$r=(1,2,3).Where({$_ -ge 2},@('First',2));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_review_where_enum_array_repeated", """
try{$r=(1,2,3).Where({$_ -ge 2},@('First','First'));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_review_where_enum_array_invalid_or", """
try{$r=(1,2,3).Where({$_ -ge 2},@('Until','Last'));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_review_where_enum_array_nested", """
try{$r=(1,2,3).Where({$_ -ge 2},@(@('First'),@('Last')));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_review_where_enum_array_boolean", """
try{$r=(1,2,3).Where({$_ -ge 2},@($true));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_review_where_enum_array_fractional", """
try{$r=(1,2,3).Where({$_ -ge 2},@(1.5));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("intrinsic_review_where_enum_array_whole_double", """
try{$r=(1,2,3).Where({$_ -ge 2},@([double]1,2));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_review_where_enum_array_typed", """
try{$r=(1,2,3).Where({$_ -ge 2},[int[]]@(1,2));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_review_where_enum_array_single_prefix", """
try{$r=(1,2,3).Where({$_ -ge 2},@('F'));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2;count=1")]),
            ("intrinsic_review_where_enum_array_nested_prefix", """
try{$r=(1,2,3).Where({$_ -ge 2},@(@('F'),@('L')));"value=$($r -join ',');count=$($r.Count)"}catch{'caught'}
""", [new("Output", "value=2,3;count=2")]),
            ("intrinsic_review_list_foreach_mutating_last", """
$a=[System.Collections.Generic.List[int]]@(1,2);$script:calls=0;try{$a.ForEach({param($item)$script:calls++;if($item -eq 2){$a.Add(3)}})}catch{'caught'};"value=$($a -join ',');calls=$script:calls"
""", [new("Output", "caught"), new("Output", "value=1,2,3;calls=2")]),
            ("intrinsic_ordered_count_readonly_setter", """
$a=[ordered]@{Count=99;Name='a'};$r='old';try{$r=$a.ForEach('Count',7)}catch{'caught'};"key=$($a['Count']);keys=$($a.Keys.Count);result=$r"
""", [new("Output", "caught"), new("Output", "key=99;keys=2;result=old")]),
            ("intrinsic_ordered_keys_readonly_setter", """
$a=[ordered]@{Name='a'};$r='old';try{$r=$a.ForEach('Keys',@('bad'))}catch{'caught'};"hasKeys=$($a.Contains('Keys'));keys=$($a.Keys -join ',');result=$r"
""", [new("Output", "caught"), new("Output", "hasKeys=False;keys=Name;result=old")]),
            // A runtime safety assertion: do not run an intentionally cyclic enum conversion in the native oracle.
            ("intrinsic_where_cyclic_mode_guard", """
$mode = [System.Collections.ArrayList]@(1); [void]$mode.Add($mode)
try { (1,2).Where({ $true }, $mode) } catch { 'caught' }
""", [new("Output", "caught")]),
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
        Console.WriteLine($"Collection intrinsic scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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
