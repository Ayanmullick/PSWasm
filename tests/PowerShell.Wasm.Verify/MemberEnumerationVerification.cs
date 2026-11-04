using PSWasm;

internal static class MemberEnumerationVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records were independently recorded with native PowerShell 7.6.1.
        // Catch markers test failure semantics without depending on localized error messages.
        // The final two cycle guards are runtime safety assertions, not native compatibility claims.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("member_property_simple", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'});$r=$a.Name;"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=a,b;count=2;array=True")]),
            ("member_property_singleton", """
$a=,@([pscustomobject]@{Name='a'});$r=$a.Name;"value=$r;count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=a;count=1;array=False")]),
            ("member_property_missing_mixed", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Other='x'},[pscustomobject]@{Name='b'});$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,,b;count=3")]),
            ("member_property_all_missing", """
$a=@([pscustomobject]@{Other='a'},[pscustomobject]@{Other='b'});$r=$a.Name;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=False;count=2;wrapped=2")]),
            ("member_property_null_values", """
$a=@([pscustomobject]@{Name=$null},[pscustomobject]@{Name='a'},[pscustomobject]@{Name=$null});$r=$a.Name;"value=$($r -join ',');count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "value=,a,;count=3;wrapped=3")]),
            ("member_property_null_elements", """
$a=@($null,[pscustomobject]@{Name='a'},$null);$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a;count=1")]),
            ("member_property_empty", """
$a=@();$r=$a.Name;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=True;count=0;wrapped=1")]),
            ("member_property_nested", """
$a=@(@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'}),@([pscustomobject]@{Name='c'}));$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,b,c;count=3")]),
            ("member_property_values_arrays", """
$a=@([pscustomobject]@{Value=@(1,2)},[pscustomobject]@{Value=@(3,4)});$r=$a.Value;"value=$($r -join ',');count=$($r.Count);firstArray=$($r[0] -is [array])"
""", [new("Output", "value=1,2,3,4;count=4;firstArray=False")]),
            ("member_property_values_nested", """
$a=@([pscustomobject]@{Value=@(@(1,2),@(3,4))},[pscustomobject]@{Value=@(5,6)});$r=$a.Value;"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',');last=$($r[-1])"
""", [new("Output", "count=4;firstArray=True;first=1,2;last=6")]),
            ("member_property_values_empty", """
$a=@([pscustomobject]@{Value=@()},[pscustomobject]@{Value=@(7)});$r=$a.Value;"value=$r;count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=7;count=1;array=False")]),
            ("member_property_direct_count", """
$a=@([pscustomobject]@{Count=99;Length=98},[pscustomobject]@{Count=97;Length=96});"count=$($a.Count);length=$($a.Length)"
""", [new("Output", "count=2;length=2")]),
            ("member_property_dictionary", """
$a=@{Name='a';Other='b'};"name=$($a.Name);missing=$(@($a.Missing).Count);count=$($a.Count)"
""", [new("Output", "name=a;missing=1;count=2")]),
            ("member_property_dictionary_array", """
$a=@(@{Name='a'},@{Name='b'});$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,b;count=2")]),
            ("member_property_dictionary_collision", """
$a=@(@{Count=99;Length=98;Keys='a'},@{Count=97;Length=96;Keys='b'});"count=$($a.Count);length=$($a.Length);keys=$($a.Keys -join ',')"
""", [new("Output", "count=2;length=2;keys=a,b")]),
            ("member_property_key_views", """
$a=@{a=1;b=2};$r=$a.Keys.Length;"value=$($r -join ',');count=$($r.Count);directCount=$($a.Keys.Count)"
""", [new("Output", "value=1,1;count=2;directCount=2")]),
            ("member_method_simple", """
$r=('a','b').ToUpper();"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=A,B;count=2;array=True")]),
            ("member_method_singleton", """
$r=(,'a').ToUpper();"value=$r;count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=A;count=1;array=False")]),
            ("member_method_empty", """
$a=@();try{$a.ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_null_elements", """
$a=@($null,'a',$null,'b');try{$r=$a.ToUpper();"value=$($r -join ',');count=$($r.Count)"}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_mixed_assignment", """
$r='before';try{$r=('a',2,'b').ToUpper()}catch{'caught'};"result=$r"
""", [new("Output", "caught"), new("Output", "result=before")]),
            ("member_method_mixed_direct", """
try{('a',2,'b').ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_nested", """
$a=@(@('a','b'),@('c'));$r=$a.ToUpper();"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=A,B,C;count=3")]),
            ("member_method_arrays_result", """
$a=@([System.Text.Encoding]::UTF8,[System.Text.Encoding]::UTF8);$r=$a.GetBytes('ab');"value=$($r -join ',');count=$($r.Count);firstArray=$($r[0] -is [array]);itemType=$($r[0].GetType().Name)"
""", [new("Output", "value=97,98,97,98;count=4;firstArray=False;itemType=Byte")]),
            ("member_method_missing", """
$a=@('a','b');try{$a.NoSuchMethod()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_argument_once", """
$i=0;$r=('abc','def').Substring(($i++));"value=$($r -join ',');calls=$i"
""", [new("Output", "value=abc,def;calls=1")]),
            ("member_method_overload_failure", """
$r='before';try{$r=('abc','x','def').Substring(2)}catch{'caught'};"result=$r"
""", [new("Output", "caught"), new("Output", "result=before")]),
            ("member_method_direct_tostring", """
$r=('a','b').ToString();"value=$r;array=$($r -is [array])"
""", [new("Output", "value=System.Object[];array=False")]),
            ("member_method_list_void", """
$a=[System.Collections.Generic.List[int]]@(1);$b=[System.Collections.Generic.List[int]]@(2);$x=@($a,$b);try{$r=$x.Add(3);"rNull=$($null -eq $r);rCount=$($r.Count)"}catch{'caught'};"a=$($a -join ',');b=$($b -join ',')"
""", [new("Output", "caught"), new("Output", "a=1;b=2")]),
            ("member_property_iterator", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'}).GetEnumerator();$r=$a.Name;"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext())"
""", [new("Output", "value=a,b;count=2;next=False")]),
            ("member_property_scalar_missing", """
$a=12;$r=$a.NoSuch;"null=$($null -eq $r);wrapped=$(@($a.NoSuch).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_property_strings_missing", """
$a='a','b';$r=$a.NoSuch;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.NoSuch).Count)"
""", [new("Output", "null=True;count=0;wrapped=1")]),
            ("member_property_numbers_missing", """
$a=1,2;$r=$a.NoSuch;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.NoSuch).Count)"
""", [new("Output", "null=True;count=0;wrapped=1")]),
            ("member_property_mixed_missing", """
$a=@([pscustomobject]@{Name='a'},3,'b',[pscustomobject]@{Other=4});$r=$a.Name;"value=$($r -join ',');count=$($r.Count);lastNull=$($null -eq $r[-1])"
""", [new("Output", "value=a,;count=2;lastNull=True")]),
            ("member_property_missing_custom_single", """
$a=,[pscustomobject]@{Other='x'};$r=$a.Name;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=True;count=0;wrapped=1")]),
            ("member_property_null_only", """
$a=@($null,$null);$r=$a.Name;"null=$($null -eq $r);count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=True;count=0;wrapped=1")]),
            ("member_property_null_single", """
$a=,$null;$r=$a.Name;"null=$($null -eq $r);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_property_value_null_single", """
$a=,[pscustomobject]@{Name=$null};$r=$a.Name;"null=$($null -eq $r);wrapped=$(@($a.Name).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_property_values_all_empty", """
$a=@([pscustomobject]@{Value=@()},[pscustomobject]@{Value=@()});$r=$a.Value;"null=$($null -eq $r);wrapped=$(@($a.Value).Count);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "null=True;wrapped=1;array=False;count=0")]),
            ("member_property_values_null_and_empty", """
$a=@([pscustomobject]@{Value=@()},[pscustomobject]@{Value=$null},[pscustomobject]@{Value=@()});$r=$a.Value;"null=$($null -eq $r);wrapped=$(@($a.Value).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_property_values_arrays_null", """
$a=@([pscustomobject]@{Value=@(1,$null,2)},[pscustomobject]@{Value=@($null)});$r=$a.Value;"value=$($r -join ',');count=$($r.Count);lastNull=$($null -eq $r[-1])"
""", [new("Output", "value=1,,2,;count=4;lastNull=True")]),
            ("member_property_nested_empty", """
$a=@(@(),@([pscustomobject]@{Name='a'}),@());$r=$a.Name;"value=$($r -join ',');count=$($r.Count);wrapped=$(@($a.Name).Count)"
""", [new("Output", "value=a;count=1;wrapped=1")]),
            ("member_property_nested_missing", """
$a=@(@(1,2),@([pscustomobject]@{Name='a'}),@('x'));$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a;count=1")]),
            ("member_property_nested_actual_null", """
$a=@(@($null),@([pscustomobject]@{Name='a'}));$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a;count=1")]),
            ("member_property_custom_value_scalar", """
$a=,[pscustomobject]@{Value=[pscustomobject]@{Name='x'}};$r=$a.Value;"name=$($r.Name);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "name=x;array=False;count=1")]),
            ("member_property_dictionary_value_scalar", """
$a=,[pscustomobject]@{Value=@{Name='x';Other='y'}};$r=$a.Value;"name=$($r.Name);array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "name=x;array=False;count=2")]),
            ("member_property_bytes_values", """
$a=@([pscustomobject]@{Value=[byte[]]@(1,2)},[pscustomobject]@{Value=[byte[]]@(3)});$r=$a.Value;"value=$($r -join ',');count=$($r.Count);type=$($r[0].GetType().Name)"
""", [new("Output", "value=1,2,3;count=3;type=Byte")]),
            ("member_property_bytes_missing", """
$a=[byte[]]@(1,2);$r=$a.NoSuch;"null=$($null -eq $r);wrapped=$(@($a.NoSuch).Count);type=$($a.GetType().Name);count=$($a.Count);length=$($a.Length)"
""", [new("Output", "null=True;wrapped=1;type=Byte[];count=2;length=2")]),
            ("member_property_list_length", """
$a=[System.Collections.Generic.List[string]]@('abc','xy');$r=$a.Length;"value=$($r -join ',');count=$($r.Count);direct=$($a.Count)"
""", [new("Output", "value=3,2;count=2;direct=2")]),
            ("member_property_list_count_precedence", """
$a=[System.Collections.Generic.List[object]]@([pscustomobject]@{Count=99;Length=5},[pscustomobject]@{Count=98;Length=6});"count=$($a.Count);length=$($a.Length -join ',')"
""", [new("Output", "count=2;length=5,6")]),
            ("member_property_arraylist_length", """
$a=[System.Collections.ArrayList]@('abc','xy');"count=$($a.Count);length=$($a.Length -join ',')"
""", [new("Output", "count=2;length=3,2")]),
            ("member_property_iterator_count", """
$a=@('abc','xy').GetEnumerator();$r=$a.Count;"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext())"
""", [new("Output", "value=1,1;count=2;next=False")]),
            ("member_property_iterator_length", """
$a=@('abc','xy').GetEnumerator();$r=$a.Length;"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext())"
""", [new("Output", "value=3,2;count=2;next=False")]),
            ("member_property_dictionary_iterator", """
$a=[ordered]@{first=1;second=2};$r=$a.GetEnumerator().Key;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=;count=0")]),
            ("member_property_ordered_keys", """
$a=[ordered]@{aa=1;bbb=2};"length=$($a.Keys.Length -join ',');keysCount=$($a.Keys.Count)"
""", [new("Output", "length=2,3;keysCount=2")]),
            ("member_property_null_scalar", """
$r=$null.NoSuch;"null=$($null -eq $r);wrapped=$(@($null.NoSuch).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_property_case_insensitive", """
$a=@([pscustomobject]@{Name='a'},[pscustomobject]@{NAME='b'});"value=$($a.nAmE -join ',')"
""", [new("Output", "value=a,b")]),
            ("member_property_direct_result_shape", """
$a=,[pscustomobject]@{Name='a'};"directArray=$($a -is [array]);resultArray=$($a.Name -is [array]);wrapped=$(@($a.Name).Count)"
""", [new("Output", "directArray=True;resultArray=False;wrapped=1")]),
            ("member_method_null_scalar", """
try{$null.ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_null_after_success", """
$a=@('a',$null,'b');try{$a.ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_nested_empty_first", """
$a=@(@(),@('a'));try{$r=$a.ToUpper();"value=$r;array=$($r -is [array])"}catch{'caught'}
""", [new("Output", "value=A;array=False")]),
            ("member_method_nested_empty_last", """
$a=@(@('a'),@());try{$r=$a.ToUpper();"value=$r;array=$($r -is [array])"}catch{'caught'}
""", [new("Output", "value=A;array=False")]),
            ("member_method_nested_all_empty", """
$a=@(@(),@());try{$a.ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_nested_null", """
$a=@(@('a'),@($null));$r='old';try{$r=$a.ToUpper()}catch{'caught'};"result=$r"
""", [new("Output", "caught"), new("Output", "result=old")]),
            ("member_method_case_insensitive", """
$r=('a','b').tOuPpErInVaRiAnT();"value=$($r -join ',')"
""", [new("Output", "value=A,B")]),
            ("member_method_iterator", """
$a=@('a','b').GetEnumerator();$r=$a.ToUpperInvariant();"value=$($r -join ',');count=$($r.Count);next=$($a.MoveNext())"
""", [new("Output", "value=A,B;count=2;next=False")]),
            ("member_method_void_iterator", """
$a=[System.Collections.Generic.List[int]]@(1);$b=[System.Collections.Generic.List[int]]@(2);$x=@($a,$b).GetEnumerator();$r=$x.Add(3);"null=$($null -eq $r);wrapped=$(@($r).Count);a=$($a -join ',');b=$($b -join ',')"
""", [new("Output", "null=True;wrapped=0;a=1,3;b=2,3")]),
            ("member_method_void_partial_failure", """
$a=[System.Collections.Generic.List[int]]@(1);$x=@($a,'bad').GetEnumerator();$r='old';try{$r=$x.Add(3)}catch{'caught'};"result=$r;a=$($a -join ',')"
""", [new("Output", "caught"), new("Output", "result=old;a=1,3")]),
            ("member_method_result_partial_failure", """
$a=[System.Collections.ArrayList]@(1);$x=@($a,'bad').GetEnumerator();try{$x.Add(3)}catch{'caught'};"a=$($a -join ',')"
""", [new("Output", "caught"), new("Output", "a=1,3")]),
            ("member_method_argument_side_effect_failure", """
$i=0;$r='old';try{$r=('abc','x').Substring((++$i)+1)}catch{'caught'};"result=$r;calls=$i"
""", [new("Output", "caught"), new("Output", "result=old;calls=1")]),
            ("member_method_arguments_multiple_once", """
$i=0;$r=('abcdef','uvwxyz').Substring(($i++),(++$i));"value=$($r -join ',');calls=$i"
""", [new("Output", "value=ab,uv;calls=2")]),
            ("member_method_argument_nested_once", """
$i=0;$a=@(@('abc','def'),@('ghi'));$r=$a.Substring(($i++));"value=$($r -join ',');calls=$i"
""", [new("Output", "value=abc,def,ghi;calls=1")]),
            ("member_method_empty_args_evaluated", """
$i=0;try{@().Substring(($i++))}catch{'caught'};"calls=$i"
""", [new("Output", "caught"), new("Output", "calls=1")]),
            ("member_method_null_args_evaluated", """
$i=0;try{$null.Substring(($i++))}catch{'caught'};"calls=$i"
""", [new("Output", "caught"), new("Output", "calls=1")]),
            ("member_method_direct_gettype", """
$a='a','b';"type=$($a.GetType().Name);array=$($a -is [array])"
""", [new("Output", "type=Object[];array=True")]),
            ("member_method_direct_getenumerator", """
$a='a','b';$r=$a.GetEnumerator();"count=$(@($r).Count)"
""", [new("Output", "count=2")]),
            ("member_method_dictionary_scalar", """
$a=@{one='a';two='b'};try{$a.ToUpper()}catch{'caught'};"count=$($a.Count)"
""", [new("Output", "caught"), new("Output", "count=2")]),
            ("member_method_dictionary_values", """
$a=[ordered]@{one='a';two='b'};$r=$a.Values.ToUpperInvariant();"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=A,B;count=2")]),
            ("member_method_bytes_missing", """
$a=[byte[]]@(1,2);try{$a.ToUpper()}catch{'caught'};"type=$($a.GetType().Name)"
""", [new("Output", "caught"), new("Output", "type=Byte[]")]),
            ("member_method_empty_results", """
$a=@([System.Text.Encoding]::UTF8,[System.Text.Encoding]::UTF8);$r=$a.GetBytes('');"null=$($null -eq $r);array=$($r -is [array]);count=$($r.Count);wrapped=$(@($a.GetBytes('')).Count)"
""", [new("Output", "null=True;array=False;count=0;wrapped=0")]),
            ("member_method_arraylist_results", """
$a=[System.Collections.ArrayList]@(1,2);$b=[System.Collections.ArrayList]@(3,4);$x=@($a,$b);$r=$x.ToArray();"value=$($r -join ',');count=$($r.Count);firstArray=$($r[0] -is [array])"
""", [new("Output", "value=1,2,3,4;count=4;firstArray=False")]),
            ("member_method_empty_property_chain", """
$a=@();try{$r=$a.NoSuch.ToUpper()}catch{'caught'};"null=$($null -eq $r)"
""", [new("Output", "caught"), new("Output", "null=True")]),
            ("member_property_values_iterator", """
$a=,[pscustomobject]@{Value=@(1,2).GetEnumerator()};$r=$a.Value;"value=$($r -join ',');count=$($r.Count);array=$($r -is [array])"
""", [new("Output", "value=1,2;count=2;array=True")]),
            ("member_property_iterator_nested", """
$a=@(@([pscustomobject]@{Name='a'}),@([pscustomobject]@{Name='b'},[pscustomobject]@{Name='c'})).GetEnumerator();$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,b,c;count=3")]),
            ("member_property_iterator_lists_length", """
$a=[System.Collections.Generic.List[string]]@('aa','bbb');$b=[System.Collections.Generic.List[string]]@('c','dddd');$x=@($a,$b).GetEnumerator();$r=$x.Length;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,1;count=2")]),
            ("member_property_iterator_lists_count", """
$a=[System.Collections.Generic.List[string]]@('aa','bbb');$b=[System.Collections.Generic.List[string]]@('c');$x=@($a,$b).GetEnumerator();$r=$x.Count;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=2,1;count=2")]),
            ("member_property_iterator_custom_count", """
$x=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b';Other=2}).GetEnumerator();$r=$x.Count;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,1;count=2")]),
            ("member_property_nested_dictionary_missing", """
$a=@(@{Other='a'},@{Other='b'});$r=$a.Name;"null=$($null -eq $r);wrapped=$(@($a.Name).Count);count=$($r.Count)"
""", [new("Output", "null=True;wrapped=1;count=0")]),
            ("member_property_keyview_missing", """
$a=@{a=1;b=2};$r=$a.Keys.NoSuch;"null=$($null -eq $r);wrapped=$(@($a.Keys.NoSuch).Count)"
""", [new("Output", "null=True;wrapped=1")]),
            ("member_method_contains_precedence", """
$r=('alpha','beta').Contains('alpha');"value=$r;array=$($r -is [array]);count=$($r.Count)"
""", [new("Output", "value=True;array=False;count=1")]),
            ("member_method_direct_bad_overload", """
try{(1,2).ToString('D2')}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_method_enumerated_bad_overload", """
$i=0;try{('a','b').ToUpperInvariant(($i++))}catch{'caught'};"calls=$i"
""", [new("Output", "caught"), new("Output", "calls=1")]),
            ("member_method_list_remove_values", """
$a=[System.Collections.Generic.List[int]]@(1,2);$b=[System.Collections.Generic.List[int]]@(3);$x=@($a,$b).GetEnumerator();$r=$x.Remove(2);"values=$($r -join ',');count=$($r.Count);a=$($a -join ',');b=$($b -join ',')"
""", [new("Output", "values=True,False;count=2;a=1;b=3")]),
            ("member_method_array_bytes_tostring", """
$a=[byte[]]@(1,2);$r=$a.ToString();"value=$r;array=$($r -is [array])"
""", [new("Output", "value=System.Byte[];array=False")]),
            ("member_property_view_nested_length", """
$a=[ordered]@{aa=1;bbb=2};$b=[ordered]@{cccc=3};$x=@($a.Keys,$b.Keys).GetEnumerator();$r=$x.Length;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=1,1;count=2")]),
            ("member_property_missing_and_null", """
$a=@(1,$null,[pscustomobject]@{Value=$null},[pscustomobject]@{Other=2},'x');$r=$a.Value;"null=$($null -eq $r);value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "null=False;value=,;count=2")]),
            ("member_property_empty_member_array_identity", """
$a=,[pscustomobject]@{Value=@(,@(1,2))};$r=$a.Value;"value=$($r -join ',');count=$($r.Count);array=$($r -is [array]);firstArray=$($r[0] -is [array])"
""", [new("Output", "value=1,2;count=2;array=True;firstArray=False")]),
            ("member_method_results_nested_depth", """
$a=[System.Collections.ArrayList]@(@(1,2),@(3,4));$b=[System.Collections.ArrayList]@(5);$r=@($a,$b).ToArray();"count=$($r.Count);firstArray=$($r[0] -is [array]);first=$($r[0] -join ',');last=$($r[-1])"
""", [new("Output", "count=3;firstArray=True;first=1,2;last=5")]),
            ("member_property_iterator_real_current", """
$a=@([pscustomobject]@{Current='item'}).GetEnumerator();try{$r=$a.Current;"null=$($null -eq $r)"}catch{'caught'};"advance=$($a.MoveNext());current=$($a.Current.Current)"
""", [new("Output", "null=True"), new("Output", "advance=True;current=item")]),
            ("member_property_ordered_dictionary_missing", """
$rows=@([ordered]@{Name='one'},[ordered]@{Name='two'});$r=@($rows[0],$null,$rows[1]).Missing;"count=$($r.Count);wrapped=$(@($r).Count);null=$($null -eq $r)"
""", [new("Output", "count=0;wrapped=1;null=True")]),
            ("member_method_computed_evaluation_once", """
$script:targets=0;$script:names=0;$i=0;function Get-Targets{$script:targets++;'abc';'def'};function Get-Name{$script:names++;'Substring'};$r=(Get-Targets).(Get-Name)((++$i));"value=$($r -join ',');targets=$script:targets;names=$script:names;args=$i"
""", [new("Output", "value=bc,ef;targets=1;names=1;args=1")]),
            ("member_method_computed_property_once", """
$script:targets=0;$script:names=0;function Get-Targets{$script:targets++;[pscustomobject]@{Name='a'};[pscustomobject]@{Name='b'}};function Get-Name{$script:names++;'Name'};$r=(Get-Targets).(Get-Name);"value=$($r -join ',');targets=$script:targets;names=$script:names"
""", [new("Output", "value=a,b;targets=1;names=1")]),
            ("member_method_nullconditional_arguments", """
$a=$null;$i=0;$r=${a}?.Substring((++$i));"null=$($null -eq $r);args=$i;wrapped=$(@($r).Count)"
""", [new("Output", "null=True;args=0;wrapped=1")]),
            ("member_method_nullconditional_computed", """
$script:targets=0;$script:names=0;$i=0;function Get-Target{$script:targets++;$null};function Get-Name{$script:names++;'Substring'};$r=(Get-Target)?.(Get-Name)((++$i));"null=$($null -eq $r);targets=$script:targets;names=$script:names;args=$i"
""", [new("Output", "null=True;targets=1;names=0;args=0")]),
            ("member_method_nullconditional_nonnull", """
$a='abc','def';$script:names=0;$i=0;function Get-Name{$script:names++;'Substring'};$r=${a}?.(Get-Name)((++$i));"value=$($r -join ',');names=$script:names;args=$i"
""", [new("Output", "value=bc,ef;names=1;args=1")]),
            ("member_method_foreach_invalid_argument_once", """
$i=98;try{(1,2).ForEach((++$i),1,2)}catch{'caught'};"argument=$i"
""", [new("Output", "caught"), new("Output", "argument=99")]),
            ("member_method_where_invalid_argument_once", """
$i=98;try{(1,2).Where((++$i))}catch{'caught'};"argument=$i"
""", [new("Output", "caught"), new("Output", "argument=99")]),
            ("member_method_foreach_computed_invalid_once", """
$script:targets=0;$script:names=0;$i=98;function Get-Targets{$script:targets++;1;2};function Get-Name{$script:names++;'ForEach'};try{(Get-Targets).(Get-Name)((++$i),1,2)}catch{'caught'};"targets=$script:targets;names=$script:names;argument=$i"
""", [new("Output", "caught"), new("Output", "targets=1;names=1;argument=99")]),
            ("member_method_where_computed_invalid_once", """
$script:targets=0;$script:names=0;$i=98;function Get-Targets{$script:targets++;1;2};function Get-Name{$script:names++;'Where'};try{(Get-Targets).(Get-Name)((++$i))}catch{'caught'};"targets=$script:targets;names=$script:names;argument=$i"
""", [new("Output", "caught"), new("Output", "targets=1;names=1;argument=99")]),
            ("member_method_list_tostring", """
$a=[System.Collections.Generic.List[int]]@(1,2);$r=$a.ToString();"value=$r;array=$($r -is [array])"
""", [new("Output", "value=System.Collections.Generic.List`1[System.Int32];array=False")]),
            ("member_method_list_iterator_tostring", """
$a=[System.Collections.Generic.List[int]]@(1,2);$x=$a.GetEnumerator();$r=$x.ToString();"value=$r;array=$($r -is [array])"
""", [new("Output", "value=System.Collections.Generic.List`1+Enumerator[System.Int32];array=False")]),
            ("member_method_arraylist_tostring", """
$a=[System.Collections.ArrayList]@(1,2);$r=$a.ToString();"value=$r;array=$($r -is [array])"
""", [new("Output", "value=System.Collections.ArrayList;array=False")]),
            ("member_property_repeated_child", """
$child=@([pscustomobject]@{Name='a'},[pscustomobject]@{Name='b'});$a=@($child,$child);$r=$a.Name;"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=a,b,a,b;count=4")]),
            ("member_method_repeated_child", """
$child='a','b';$a=@($child,$child);$r=$a.ToUpperInvariant();"value=$($r -join ',');count=$($r.Count)"
""", [new("Output", "value=A,B,A,B;count=4")]),
            ("member_conditional_named_property_receiver_twice", """
$script:calls=0;function Get-Target{$script:calls++;[pscustomobject]@{Name='a'}};$r=(Get-Target)?.Name;"value=$r;calls=$script:calls"
""", [new("Output", "value=a;calls=2")]),
            ("member_conditional_computed_property_second_value", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';[pscustomobject]@{Name="value$script:calls"}};function Get-Name{$script:trace+='N';'Name'};$r=(Get-Target)?.(Get-Name);"value=$r;calls=$script:calls;trace=$script:trace"
""", [new("Output", "value=value2;calls=2;trace=TTN")]),
            ("member_conditional_named_method_second_value", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){'abc'}else{'xyz'}};function Get-Argument{$script:trace+='A';1};$r=(Get-Target)?.Substring((Get-Argument));"value=$r;calls=$script:calls;trace=$script:trace"
""", [new("Output", "value=yz;calls=2;trace=TTA")]),
            ("member_conditional_computed_method_second_value", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){'abc'}else{'xyz'}};function Get-Name{$script:trace+='N';'Substring'};function Get-Argument{$script:trace+='A';1};$r=(Get-Target)?.(Get-Name)((Get-Argument));"value=$r;calls=$script:calls;trace=$script:trace"
""", [new("Output", "value=yz;calls=2;trace=TTNA")]),
            ("member_conditional_named_property_first_null", """
$script:calls=0;function Get-Target{$script:calls++;if($script:calls -eq 1){$null}else{[pscustomobject]@{Name='unexpected'}}};$r=(Get-Target)?.Name;"null=$($null -eq $r);calls=$script:calls"
""", [new("Output", "null=True;calls=1")]),
            ("member_conditional_computed_property_first_null", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';$null};function Get-Name{$script:trace+='N';'Name'};$r=(Get-Target)?.(Get-Name);"null=$($null -eq $r);calls=$script:calls;trace=$script:trace"
""", [new("Output", "null=True;calls=1;trace=T")]),
            ("member_conditional_named_method_first_null", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';$null};function Get-Argument{$script:trace+='A';1};$r=(Get-Target)?.Substring((Get-Argument));"null=$($null -eq $r);calls=$script:calls;trace=$script:trace"
""", [new("Output", "null=True;calls=1;trace=T")]),
            ("member_conditional_named_property_second_null", """
$script:calls=0;function Get-Target{$script:calls++;if($script:calls -eq 1){[pscustomobject]@{Name='first'}}else{$null}};$r=(Get-Target)?.Name;"null=$($null -eq $r);calls=$script:calls"
""", [new("Output", "null=True;calls=2")]),
            ("member_conditional_computed_property_second_null", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){[pscustomobject]@{Name='first'}}else{$null}};function Get-Name{$script:trace+='N';'Name'};$r=(Get-Target)?.(Get-Name);"null=$($null -eq $r);calls=$script:calls;trace=$script:trace"
""", [new("Output", "null=True;calls=2;trace=TTN")]),
            ("member_conditional_named_method_second_null", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){'abc'}else{$null}};function Get-Argument{$script:trace+='A';1};try{(Get-Target)?.Substring((Get-Argument))}catch{'caught'};"calls=$script:calls;trace=$script:trace"
""", [new("Output", "caught"), new("Output", "calls=2;trace=TTA")]),
            ("member_conditional_computed_method_second_null", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){'abc'}else{$null}};function Get-Name{$script:trace+='N';'Substring'};function Get-Argument{$script:trace+='A';1};try{(Get-Target)?.(Get-Name)((Get-Argument))}catch{'caught'};"calls=$script:calls;trace=$script:trace"
""", [new("Output", "caught"), new("Output", "calls=2;trace=TTNA")]),
            ("member_conditional_computed_method_second_receiver_throws", """
$script:calls=0;$script:trace='';function Get-Target{$script:calls++;$script:trace+='T';if($script:calls -eq 1){'abc'}else{throw 'stop'}};function Get-Name{$script:trace+='N';'Substring'};function Get-Argument{$script:trace+='A';1};try{(Get-Target)?.(Get-Name)((Get-Argument))}catch{'caught'};"calls=$script:calls;trace=$script:trace"
""", [new("Output", "caught"), new("Output", "calls=2;trace=TT")]),
            ("member_runtime_property_cycle_guard", """
$a=,0;$a[0]=$a;try{$a.Name}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("member_runtime_method_cycle_guard", """
$a=,0;$a[0]=$a;try{$a.ToUpper()}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
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
        Console.WriteLine($"Member enumeration scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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
