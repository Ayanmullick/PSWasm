using PSWasm;

internal static class TypedVariableVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records were independently recorded with native PowerShell 7.6.1.
        // Constraint failures use catch markers so coverage does not depend on localized error text.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("typed_reassign_converts", """
[int]$value='2'; $value='3'; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=3;int=True")]),
            ("typed_parallel_targets", """
[int]$a,[string]$b = 1.2,3; "a=$a;ai=$($a -is [int]);b=$b;bs=$($b -is [string])"; $a='4';$b=5;"later=$a/$b/$($a -is [int])/$($b -is [string])"
""", [new("Output", "a=1;ai=True;b=3;bs=True"), new("Output", "later=4/5/True/True")]),
            ("typed_assignment_expression_value", """
$first=([int]$value='3'); $second=($value='4'); "first=$first/$($first -is [int]);second=$second/$($second -is [int]);value=$value"
""", [new("Output", "first=3/True;second=4/False;value=4")]),
            ("typed_chained_assignment", """
$a=[int]$b='2'; "a=$a/$($a -is [int]);b=$b/$($b -is [int])"; $a='word'; $b='3'; "later=$a/$b/$($b -is [int])"
""", [new("Output", "a=2/True;b=2/True"), new("Output", "later=word/3/True")]),
            ("typed_multiple_constraints", """
[int][string]$x=1.2; "value=$x;int=$($x -is [int]);string=$($x -is [string])"; $x='2.8'; "later=$x;int=$($x -is [int]);string=$($x -is [string])"
""", [new("Output", "value=1;int=True;string=False"), new("Output", "later=3;int=True;string=False")]),
            ("typed_redeclare_new_type", """
[int]$x=1; [string]$x='x'; "value=$x;string=$($x -is [string])"; $x=2; "later=$x;string=$($x -is [string])"
""", [new("Output", "value=x;string=True"), new("Output", "later=2;string=True")]),
            ("typed_bare_uninitialized_expression", """
[int]$value; "null=$($null -eq $value)"; try { $value='word'; "value=$value;string=$($value -is [string])" } catch { 'rejected' }
""", [new("Output", "0"), new("Output", "null=True"), new("Output", "value=word;string=True")]),
            ("typed_bare_existing_expression", """
$value='2'; [int]$value; "original=$value;string=$($value -is [string])"; $value='word'; $value
""", [new("Output", "2"), new("Output", "original=2;string=True"), new("Output", "word")]),
            ("typed_rhs_cast_not_constraint", """
$value=[int]'1'; $value='word'; "value=$value;string=$($value -is [string])"
""", [new("Output", "value=word;string=True")]),
            ("typed_parenthesized_parallel_output", """
$result=([int]$a,[string]$b=1.2,3); "count=$($result.Count);first=$($result[0]);firstint=$($result[0] -is [int]);second=$($result[1]);secondstring=$($result[1] -is [string])"
""", [new("Output", "count=2;first=1.2;firstint=False;second=3;secondstring=False")]),
            ("typed_string_conversion_persists", """
[string]$value=1; $value=2; "value=$value;string=$($value -is [string])"; $value=@('a','b'); "array=$value;string=$($value -is [string])"
""", [new("Output", "value=2;string=True"), new("Output", "array=a b;string=True")]),
            ("typed_boolean_conversion_persists", """
[bool]$value=$false; try { $value='false' } catch { 'string-rejected' }; "value=$value;bool=$($value -is [bool])"; try { $value=$null } catch { 'null-rejected' }; "null=$value"
""", [new("Output", "string-rejected"), new("Output", "value=False;bool=True"), new("Output", "null-rejected"), new("Output", "null=False")]),
            ("typed_int_null_converts_zero", """
[int]$value=7; $value=$null; "value=$value;int=$($value -is [int]);null=$($null -eq $value)"
""", [new("Output", "value=0;int=True;null=False")]),
            ("typed_string_null_converts_empty", """
[string]$value='before'; $value=$null; "value=[$value];string=$($value -is [string]);null=$($null -eq $value)"
""", [new("Output", "value=[];string=True;null=False")]),
            ("typed_failed_write_preserves_value", """
[int]$value=7; try { $value='bad' } catch { 'rejected' }; "value=$value;int=$($value -is [int])"
""", [new("Output", "rejected"), new("Output", "value=7;int=True")]),
            ("typed_byte_overflow_preserves_value", """
[byte]$value=255; try { $value=256 } catch { 'overflow' }; try { $value=-1 } catch { 'negative' }; "value=$value;byte=$($value -is [byte])"
""", [new("Output", "overflow"), new("Output", "negative"), new("Output", "value=255;byte=True")]),
            ("typed_fractional_integer_rounding", """
[int]$value=0; $value=1.5; $value; $value=2.5; $value; $value=-1.5; $value
""", [new("Output", "2"), new("Output", "2"), new("Output", "-2")]),
            ("typed_repeated_same_declaration", """
[int]$value=1; [int]$value='2'; $value='3'; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=3;int=True")]),
            ("typed_failed_redeclaration_preserves_constraint", """
[int]$value=1; try { [byte]$value=300 } catch { 'rejected' }; $value=256; "value=$value;int=$($value -is [int]);byte=$($value -is [byte])"
""", [new("Output", "rejected"), new("Output", "value=256;int=True;byte=False")]),
            ("typed_multiple_constraints_outer_string", """
[string][int]$value=1.2; "value=$value;string=$($value -is [string])"; $value=3.6; "later=$value;string=$($value -is [string])"
""", [new("Output", "value=1;string=True"), new("Output", "later=4;string=True")]),
            ("typed_rhs_cast_still_subject_to_constraint", """
[int]$value=1; $value=[string]'2'; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=2;int=True")]),
            ("typed_compound_integer_add", """
[int]$value=1; $value+='2'; "value=$value;int=$($value -is [int])"; $value*=3; $value-=1; $value
""", [new("Output", "value=3;int=True"), new("Output", "8")]),
            ("typed_compound_string_add", """
[string]$value='1'; $value+=2; "value=$value;string=$($value -is [string])"
""", [new("Output", "value=12;string=True")]),
            ("typed_compound_division_result", """
[int]$value=5; $result=($value/=2); "stored=$value;storedint=$($value -is [int]);result=$result;resultdouble=$($result -is [double])"
""", [new("Output", "stored=2;storedint=True;result=2.5;resultdouble=True")]),
            ("typed_increment_string_results", """
[string]$value='1'; try { $post=$value++ } catch { 'post-rejected' }; try { $pre=++$value } catch { 'pre-rejected' }; "stored=$value;string=$($value -is [string]);post=$post;poststring=$($post -is [string]);pre=$pre;prestring=$($pre -is [string])"
""", [new("Output", "post-rejected"), new("Output", "pre-rejected"), new("Output", "stored=1;string=True;post=;poststring=False;pre=;prestring=False")]),
            ("typed_increment_overflow_preserves", """
[byte]$value=255; try { ++$value } catch { 'overflow' }; "value=$value;byte=$($value -is [byte])"
""", [new("Output", "overflow"), new("Output", "value=255;byte=True")]),
            ("typed_parallel_existing_targets", """
[int]$a=1; [string]$b='x'; $a,$b='2',3; "a=$a/$($a -is [int]);b=$b/$($b -is [string])"
""", [new("Output", "a=2/True;b=3/True")]),
            ("typed_parallel_failure_preserves_prior_success", """
[int]$a=1; [int]$b=2; try { $a,$b='3','bad' } catch { 'rejected' }; "a=$a;b=$b"
""", [new("Output", "rejected"), new("Output", "a=3;b=2")]),
            ("typed_array_reassignment", """
[int[]]$value=1,2; $value='3','4'; "type=$($value.GetType().Name);values=$($value -join ',')"; $value='5'; "scalar=$($value.Count)/$($value[0])/$($value.GetType().Name)"
""", [new("Output", "type=Int32[];values=3,4"), new("Output", "scalar=1/5/Int32[]")]),
            ("typed_array_null_and_empty", """
[int[]]$value=1,2; $value=$null; "null=$($null -eq $value)"; $value=@(); "empty=$($value.Count)/$($value.GetType().Name)"
""", [new("Output", "null=True"), new("Output", "empty=0/Int32[]")]),
            ("typed_array_append_conversion", """
[int[]]$value=1,2; $value+='3'; "type=$($value.GetType().Name);values=$($value -join ',')"
""", [new("Output", "type=Int32[];values=1,2,3")]),
            ("typed_array_append_failure", """
[int[]]$value=1,2; try { $value+='bad' } catch { 'rejected' }; "type=$($value.GetType().Name);values=$($value -join ',')"
""", [new("Output", "rejected"), new("Output", "type=Int32[];values=1,2")]),
            ("typed_array_append_expression_result", """
[int[]]$value=1,2; $result=($value+='3'); "stored=$($value.GetType().Name);result=$($result.GetType().Name);laststring=$($result[2] -is [string]);values=$($value -join ',')"
""", [new("Output", "stored=Int32[];result=Object[];laststring=True;values=1,2,3")]),
            ("typed_array_element_mutation", """
[int[]]$value=1,2; $alias=$value; $value[0]='3'; $value[1]+='4'; "values=$($value -join ',');alias=$($alias -join ',');type=$($value.GetType().Name)"
""", [new("Output", "values=3,6;alias=3,6;type=Int32[]")]),
            ("typed_array_cast_rhs_does_not_constrain_variable", """
$value=[int[]]@(1,2); $value+='3'; "type=$($value.GetType().Name);laststring=$($value[2] -is [string])"
""", [new("Output", "type=Object[];laststring=True")]),
            ("typed_array_constraint_wraps_scalar", """
[array]$value=1; $value=2; "type=$($value.GetType().Name);count=$($value.Count);first=$($value[0])"
""", [new("Output", "type=Object[];count=1;first=2")]),
            ("typed_child_assignment_shadows_parent_constraint", """
[int]$value=1; function Invoke-Example { $value='word'; "inside=$value/$($value -is [string])" }; Invoke-Example; "outside=$value/$($value -is [int])"
""", [new("Output", "inside=word/True"), new("Output", "outside=1/True")]),
            ("typed_dot_assignment_uses_parent_constraint", """
[int]$value=1; . { $value='3' }; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=3;int=True")]),
            ("typed_dot_declaration_persists", """
$value='before'; . { [int]$value=2 }; $value='3'; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=3;int=True")]),
            ("typed_call_declaration_is_local", """
$value='before'; & { [int]$value=2; $value='3'; "inside=$value/$($value -is [int])" }; $value='after'; "outside=$value/$($value -is [string])"
""", [new("Output", "inside=3/True"), new("Output", "outside=after/True")]),
            ("typed_callback_assignment_preserves_constraint", """
[int]$value=1; 2,3 | ForEach-Object { $value=[string]$_ }; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=3;int=True")]),
            ("typed_set_variable_conversion", """
[int]$value=1; Set-Variable -Name value -Value '2'; "value=$value;int=$($value -is [int])"
""", [new("Output", "value=2;int=True")]),
            ("typed_set_variable_failure", """
[int]$value=1; try { Set-Variable -Name value -Value 'bad' -ErrorAction Stop } catch { 'rejected' }; "value=$value;int=$($value -is [int])"
""", [new("Output", "rejected"), new("Output", "value=1;int=True")]),
            ("typed_clear_scalar_defaults", """
[int]$number=7; [string]$text='before'; [bool]$flag=$true; Clear-Variable -Name number,text; try { Clear-Variable -Name flag -ErrorAction Stop } catch { 'bool-rejected' }; "number=$number;text=[$text];flag=$flag;types=$($number -is [int])/$($text -is [string])/$($flag -is [bool])"; $number='8'; "later=$number/$($number -is [int])"
""", [new("Output", "bool-rejected"), new("Output", "number=0;text=[];flag=True;types=True/True/True"), new("Output", "later=8/True")]),
            ("typed_clear_array_retains_constraint", """
[int[]]$value=1,2; Clear-Variable -Name value; "null=$($null -eq $value)"; $value='3'; "later=$($value.GetType().Name)/$($value[0])"
""", [new("Output", "null=True"), new("Output", "later=Int32[]/3")]),
            ("typed_remove_releases_constraint", """
[int]$value=1; Remove-Variable -Name value; $value='word'; "value=$value;string=$($value -is [string])"
""", [new("Output", "value=word;string=True")]),
            ("typed_failed_new_declaration_does_not_install_constraint", """
try { [int]$value='bad' } catch { 'first-rejected' }; try { $value='word'; "value=$value;string=$($value -is [string])" } catch { 'second-rejected' }
""", [new("Output", "first-rejected"), new("Output", "value=word;string=True")]),
            ("typed_assignment_failed_conversion_rhs_effect", """
[int]$value=1; $state=@{seen=0}; try { $value=$($state.seen=1; 'bad') } catch { 'rejected' }; "value=$value;seen=$($state.seen)"
""", [new("Output", "rejected"), new("Output", "value=1;seen=1")]),
            ("typed_bool_initial_string_rejected", """
try { [bool]$value='false' } catch { 'rejected' }; "null=$($null -eq $value)"; $value='untyped'; "later=$value/$($value -is [string])"
""", [new("Output", "rejected"), new("Output", "null=True"), new("Output", "later=untyped/True")]),
            ("typed_bool_initial_null_rejected", """
try { [bool]$value=$null } catch { 'rejected' }; "null=$($null -eq $value)"; $value='untyped'; "later=$value/$($value -is [string])"
""", [new("Output", "rejected"), new("Output", "null=True"), new("Output", "later=untyped/True")]),
            ("typed_bool_initial_arrays_rejected", """
try { [bool]$value=@(0) } catch { 'singleton-rejected' }; try { [bool]$value=[int[]](0,1) } catch { 'array-rejected' }; "null=$($null -eq $value)"
""", [new("Output", "singleton-rejected"), new("Output", "array-rejected"), new("Output", "null=True")]),
            ("typed_bool_numeric_conversions", """
[bool]$value=0; "zero=$value"; $value=2; "two=$value"; $value=-1; "negative=$value"; $value=0.25; "fraction=$value"; $value=0; "final=$value/$($value -is [bool])"
""", [new("Output", "zero=False"), new("Output", "two=True"), new("Output", "negative=True"), new("Output", "fraction=True"), new("Output", "final=False/True")]),
            ("typed_bool_explicit_cast_truthiness", """
[bool]$value=[bool]'false'; "string=$value"; $value=[bool]$null; "null=$value"; $value=[bool]@(0); "singleton=$value"; $value=[bool]@(0,1); "array=$value"
""", [new("Output", "string=True"), new("Output", "null=False"), new("Output", "singleton=False"), new("Output", "array=True")]),
            ("typed_bool_later_array_rejected", """
[bool]$value=$true; try { $value=@() } catch { 'empty-rejected' }; try { $value=@(0) } catch { 'singleton-rejected' }; try { $value=0,1 } catch { 'multi-rejected' }; "value=$value"
""", [new("Output", "empty-rejected"), new("Output", "singleton-rejected"), new("Output", "multi-rejected"), new("Output", "value=True")]),
            ("typed_bool_array_conversion", """
[bool[]]$value=0,2; "first=$($value -join ',')"; try { $value='false','' } catch { 'strings-rejected' }; "later=$($value -join ',')"; $value=$null; "null=$($null -eq $value)"
""", [new("Output", "first=False,True"), new("Output", "later=True,False"), new("Output", "null=True")]),
            ("typed_switch_assignment", """
[switch]$value=$true; $value=$false; "false=$value/$($value -is [switch])"; try { $value='false' } catch { 'string-rejected' }; try { $value=2 } catch { 'number-rejected' }; "two=$value"; $value=$null; "null=$value"
""", [new("Output", "false=False/True"), new("Output", "string-rejected"), new("Output", "number-rejected"), new("Output", "two=False"), new("Output", "null=False")]),
            ("typed_object_redeclaration_removes_old_constraint", """
[int]$value=1; [object]$value='word'; $value=3.5; "value=$value;double=$($value -is [double])"
""", [new("Output", "value=3.5;double=True")]),
            ("typed_alias_case_insensitive", """
[System.Int32]$VALUE='2'; $value='3'; "value=$value/$($value -is [int])"
""", [new("Output", "value=3/True")]),
            ("typed_redeclaration_failed_new_type_keeps_string", """
[string]$value='before'; try { [int]$value='bad' } catch { 'rejected' }; $value=3; "value=$value;string=$($value -is [string])"
""", [new("Output", "rejected"), new("Output", "value=3;string=True")]),
            ("typed_array_failed_redeclaration_keeps_scalar", """
[int]$value=7; try { [byte[]]$value=1,300 } catch { 'rejected' }; $value='8'; "value=$value;int=$($value -is [int])"
""", [new("Output", "rejected"), new("Output", "value=8;int=True")]),
            ("typed_array_failed_assignment_preserves_reference", """
[int[]]$value=1,2; $old=$value; try { $value='3','bad' } catch { 'rejected' }; $old[0]=9; "value=$($value -join ',');old=$($old -join ',')"
""", [new("Output", "rejected"), new("Output", "value=9,2;old=9,2")]),
            ("typed_array_assignment_raw_result", """
[int[]]$value=1; $result=($value='2','3'); "stored=$($value.GetType().Name);result=$($result.GetType().Name);first=$($result[0] -is [string]);values=$($value -join ',')"
""", [new("Output", "stored=Int32[];result=Object[];first=True;values=2,3")]),
            ("typed_array_declaration_returns_converted", """
$result=([int[]]$value='2','3'); "stored=$($value.GetType().Name);result=$($result.GetType().Name);first=$($result[0] -is [int])"
""", [new("Output", "stored=Int32[];result=Int32[];first=True")]),
            ("typed_scalar_compound_raw_result", """
[byte]$value=2; $result=($value+=3); "stored=$value/$($value -is [byte]);result=$result/$($result -is [int])"
""", [new("Output", "stored=5/True;result=5/True")]),
            ("typed_increment_returns_stored_type", """
[byte]$value=2; $before=$value++; $after=++$value; "stored=$value/$($value -is [byte]);before=$before/$($before -is [byte]);after=$after/$($after -is [byte])"
""", [new("Output", "stored=4/True;before=2/True;after=4/False")]),
            ("typed_parallel_chained_results", """
$result=([int]$first,[int[]]$rest='1','2','3'); "first=$first/$($first -is [int]);rest=$($rest.GetType().Name)/$($rest -join ',');result=$($result.GetType().Name)/$($result[0] -is [string])"
""", [new("Output", "first=1/True;rest=Int32[]/2,3;result=Object[]/True")]),
            ("typed_parallel_missing_default", """
[int]$first,[string]$second,[int[]]$rest=7; "first=$first;second=[$second];restnull=$($null -eq $rest)"; $second=8; "later=$second/$($second -is [string])"
""", [new("Output", "first=7;second=[];restnull=True"), new("Output", "later=8/True")]),
            ("typed_parallel_failure_keeps_later_untouched", """
[int]$first=1; [int]$second=2; [int]$third=3; try { $first,$second,$third='4','bad','6' } catch { 'rejected' }; "$first/$second/$third"
""", [new("Output", "rejected"), new("Output", "4/2/3")]),
            ("typed_chain_existing_constraint_raw_result", """
[int]$first=1; [string]$second='x'; $result=($first=$second=2); "first=$first/$($first -is [int]);second=$second/$($second -is [string]);result=$result/$($result -is [int])"
""", [new("Output", "first=2/True;second=2/True;result=2/True")]),
            ("typed_clear_failure_retains_bool_constraint", """
[bool]$value=$true; try { Clear-Variable value -ErrorAction Stop } catch { 'clear-rejected' }; try { $value='bad' } catch { 'later-rejected' }; "value=$value"
""", [new("Output", "clear-rejected"), new("Output", "later-rejected"), new("Output", "value=True")]),
            ("typed_set_variable_failure_retains_type", """
[byte]$value=2; try { Set-Variable value 256 -ErrorAction Stop } catch { 'rejected' }; Set-Variable value '3'; "value=$value/$($value -is [byte])"
""", [new("Output", "rejected"), new("Output", "value=3/True")]),
            ("typed_remove_and_redeclare", """
[int]$value=2; Remove-Variable value; [string]$value=3; $value=4; "value=$value/$($value -is [string])"
""", [new("Output", "value=4/True")]),
            ("typed_function_parameter_reassignment", """
function Invoke-Example { param([int]$value) $value='3'; "value=$value/$($value -is [int])"; try { $value='bad' } catch { 'rejected' }; "after=$value" }; Invoke-Example '2'
""", [new("Output", "value=3/True"), new("Output", "rejected"), new("Output", "after=3")]),
            ("typed_function_parameter_clear_remove", """
function Invoke-Example { param([int]$value) Clear-Variable value; "clear=$value/$($value -is [int])"; Remove-Variable value; $value='word'; "remove=$value/$($value -is [string])" }; Invoke-Example 2
""", [new("Output", "clear=0/True"), new("Output", "remove=word/True")]),
            ("typed_scriptblock_parameter_reassignment", """
& { param([int[]]$value) $value='3','4'; "value=$($value.GetType().Name)/$($value -join ',')"; try { $value='bad' } catch { 'rejected' } } @(1,2)
""", [new("Output", "value=Int32[]/3,4"), new("Output", "rejected")]),
            ("typed_function_assignment_shadows_parameter_constraint", """
function Invoke-Outer { param([int]$value) function Invoke-Inner { $value='word'; "inner=$value/$($value -is [string])" }; Invoke-Inner; "outer=$value/$($value -is [int])" }; Invoke-Outer 2
""", [new("Output", "inner=word/True"), new("Output", "outer=2/True")]),
            ("typed_control_flow_constraint_persists", """
if ($true) { [int]$value=1 }; $value='2'; "if=$value/$($value -is [int])"; foreach ($item in 1,2) { [string]$text=$item }; $text=3; "loop=$text/$($text -is [string])"
""", [new("Output", "if=2/True"), new("Output", "loop=3/True")]),
            ("typed_foreach_assignment_uses_constraint", """
[int]$item=0; foreach ($item in '1','2') { "item=$item/$($item -is [int])" }; "after=$item"
""", [new("Output", "item=1/True"), new("Output", "item=2/True"), new("Output", "after=2")]),
            ("typed_function_local_redeclaration_does_not_change_parent", """
[int]$value=1; function Invoke-Example { [string]$value=2; $value=3; "inside=$value/$($value -is [string])" }; Invoke-Example; $value='4'; "after=$value/$($value -is [int])"
""", [new("Output", "inside=3/True"), new("Output", "after=4/True")]),
            ("typed_function_bool_parameter_local_conversion", """
function Invoke-Example { param([bool]$value=$true) $value='false'; $value; $value=$null; $value; $value=@(0); $value }; Invoke-Example
""", [new("Output", "True"), new("Output", "False"), new("Output", "False")]),
            ("typed_function_bool_initial_local_conversion", """
function Invoke-Example { [bool]$value='false'; $value; $value=$null; $value }; Invoke-Example
""", [new("Output", "True"), new("Output", "False")]),
            ("typed_function_bool_parameter_default_strict", """
function Invoke-Example { param([bool]$value='false') 'body' }; try { Invoke-Example } catch { 'rejected' }
""", [new("Output", "rejected")]),
            ("typed_function_bool_parameter_argument_strict", """
function Invoke-Example { param([bool]$value) 'body' }; try { Invoke-Example 'false' } catch { 'rejected' }; try { Invoke-Example $null } catch { 'null-rejected' }
""", [new("Output", "rejected"), new("Output", "null-rejected")]),
            ("typed_function_bool_unoptimized_declaration", """
function Invoke-Example { . {}; [bool]$value=$true; try { $value='false'; 'assigned' } catch { 'rejected' }; $value }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "True")]),
            ("typed_function_bool_unoptimized_parameter", """
function Invoke-Example { param([bool]$value=$true) . {}; try { $value='false'; 'assigned' } catch { 'rejected' }; $value }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "True")]),
            ("typed_function_assignment_expression_converted", """
function Invoke-Example { [int]$value=1; $result=($value='2'); "result=$result/$($result -is [int]);stored=$value/$($value -is [int])" }; Invoke-Example
""", [new("Output", "result=2/True;stored=2/True")]),
            ("typed_function_unoptimized_assignment_expression_raw", """
function Invoke-Example { . {}; [int]$value=1; $result=($value='2'); "result=$result/$($result -is [int]);stored=$value/$($value -is [int])" }; Invoke-Example
""", [new("Output", "result=2/False;stored=2/True")]),
            ("typed_function_parameter_assignment_expression_converted", """
function Invoke-Example { param([int]$value=1) $result=($value='2'); "result=$result/$($result -is [int]);stored=$value/$($value -is [int])" }; Invoke-Example
""", [new("Output", "result=2/True;stored=2/True")]),
            ("typed_dot_parameter_existing_typed_removes_constraint", """
[int]$value=1; . { param([int]$value) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' } } 2; try { $value='bad'; 'after:assigned' } catch { 'after:rejected' }; "value=$value/$($value -is [string])"
""", [new("Output", "during:assigned"), new("Output", "after:assigned"), new("Output", "value=bad/True")]),
            ("typed_dot_parameter_existing_untyped_remains_untyped", """
$value='before'; . { param([int]$value) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' } } 2; try { $value='bad'; 'after:assigned' } catch { 'after:rejected' }; "value=$value/$($value -is [string])"
""", [new("Output", "during:assigned"), new("Output", "after:assigned"), new("Output", "value=bad/True")]),
            ("typed_dot_parameter_new_constraint_persists", """
. { param([int]$value) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' } } 2; try { $value='bad'; 'after:assigned' } catch { 'after:rejected' }; "value=$value/$($value -is [int])"
""", [new("Output", "during:rejected"), new("Output", "after:rejected"), new("Output", "value=2/True")]),
            ("typed_dot_parameter_inherited_creates_local_constraint", """
[int]$value=1; function Invoke-Example { . { param([int]$value) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' } } 2; try { $value='bad'; 'after:assigned' } catch { 'after:rejected' }; "inside=$value/$($value -is [int])" }; Invoke-Example; "outside=$value"
""", [new("Output", "during:rejected"), new("Output", "after:rejected"), new("Output", "inside=2/True"), new("Output", "outside=1")]),
            ("typed_callback_existing_typed_parameter_clears_constraint", """
[int]$value=1; 7 | ForEach-Object { param([int]$value=2) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' }; "during=$value" }; try { $value='bad'; 'after:assigned' } catch { 'after:rejected' }; "value=$value/$($value -is [string])"
""", [new("Output", "during:rejected"), new("Output", "during=2"), new("Output", "after:assigned"), new("Output", "value=bad/True")]),
            ("typed_callback_existing_untyped_parameter_restores_untyped", """
$value='before'; 7 | ForEach-Object { param([int]$value=2) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' }; "during=$value" }; $value='after'; "after=$value/$($value -is [string])"
""", [new("Output", "during:rejected"), new("Output", "during=2"), new("Output", "after=after/True")]),
            ("typed_callback_new_parameter_removes_constraint", """
7 | ForEach-Object { param([int]$value=2) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' }; "during=$value" }; "after-null=$($null -eq $value)"; $value='after'; "after=$value/$($value -is [string])"
""", [new("Output", "during:rejected"), new("Output", "during=2"), new("Output", "after-null=True"), new("Output", "after=after/True")]),
            ("typed_callback_inherited_parameter_restores_parent", """
[int]$value=1; function Invoke-Example { . {}; 7 | ForEach-Object { param([int]$value=2) try { $value='bad'; 'during:assigned' } catch { 'during:rejected' }; "during=$value" }; "after=$value"; $value='word'; "local=$value/$($value -is [string])" }; Invoke-Example; "outside=$value/$($value -is [int])"
""", [new("Output", "during:rejected"), new("Output", "during=2"), new("Output", "after=1"), new("Output", "local=word/True"), new("Output", "outside=1/True")]),
            ("typed_callback_declaration_persists_constraint", """
1,2 | ForEach-Object { [int]$value=$_ }; try { $value='bad' } catch { 'rejected' }; "value=$value/$($value -is [int])"
""", [new("Output", "rejected"), new("Output", "value=2/True")]),
            ("typed_intrinsic_foreach_parameter_metadata_cleanup", """
[int]$value=1; (7).ForEach({ param([int]$value) try { $value='bad' } catch { 'during:rejected' }; "during=$value" },2); $value='after'; "after=$value/$($value -is [string])"
""", [new("Output", "during:rejected"), new("Output", "during=2"), new("Output", "after=after/True")]),
            ("typed_calculated_expression_local_constraint", """
[int]$value=1; 2 | Select-Object @{Name='Seen';Expression={ [string]$value=$_; $value=3; "$value/$($value -is [string])" }} | ForEach-Object { $_.Seen }; $value='4'; "after=$value/$($value -is [int])"
""", [new("Output", "3/True"), new("Output", "after=4/True")]),
            ("typed_optimizer_prior_untyped_write", """
function Invoke-Example { $value='before'; [int]$value=1; $result=($value='2'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=String;stored=Int32")]),
            ("typed_optimizer_repeated_same_declaration", """
function Invoke-Example { [int]$value=1; [int]$value=2; $result=($value='3'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=Int32;stored=Int32")]),
            ("typed_optimizer_conflicting_declarations", """
function Invoke-Example { [int]$value=1; [string]$value=2; $result=($value=3); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=Int32;stored=String")]),
            ("typed_optimizer_later_conflict_changes_prior_bool_write", """
function Invoke-Example { param([bool]$value=$true) try { $value='false'; 'assigned' } catch { 'rejected' }; "before=$value"; [string]$value='later'; "after=$value" }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "before=True"), new("Output", "after=later")]),
            ("typed_optimizer_if_without_else", """
function Invoke-Example { if ($true) { [int]$value=1 }; $result=($value='2'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=String;stored=Int32")]),
            ("typed_optimizer_if_else_same_type", """
function Invoke-Example { if ($true) { [int]$value=1 } else { [int]$value=2 }; $result=($value='3'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=Int32;stored=Int32")]),
            ("typed_optimizer_if_else_different_types", """
function Invoke-Example { if ($true) { [int]$value=1 } else { [string]$value=2 }; $result=($value='3'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=String;stored=Int32")]),
            ("typed_optimizer_while_optional_declaration", """
function Invoke-Example { $index=0; while ($index -lt 1) { [int]$value=1; $index++ }; $result=($value='2'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=String;stored=Int32")]),
            ("typed_optimizer_do_once_declaration", """
function Invoke-Example { do { [int]$value=1 } while ($false); $result=($value='2'); "result=$($result.GetType().Name);stored=$($value.GetType().Name)" }; Invoke-Example
""", [new("Output", "result=Int32;stored=Int32")]),
            ("typed_optimizer_calculated_child_bool", """
1 | Select-Object @{Name='Value';Expression={ try { [bool]$value='false'; $value } catch { 'rejected' } }} | ForEach-Object { $_.Value }
""", [new("Output", "True")]),
            ("typed_optimizer_intrinsic_current_scope_bool", """
(1).ForEach({ try { [bool]$value='false'; $value } catch { 'rejected' } })
""", [new("Output", "rejected")]),
            ("typed_parameter_validation_persists", """
function Invoke-Example { param([ValidateSet('yes','no')][string]$value='yes') try { $value='bad'; 'assigned' } catch { 'rejected' }; "value=$value"; $value='no'; "later=$value" }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "value=yes"), new("Output", "later=no")]),
            ("typed_member_assignment_converts_write_only", """
$object=@{Value=1}; [int]$object.Value='2'; "type=$($object.Value.GetType().Name)"; $object.Value='word'; "later=$($object.Value)"
""", [new("Output", "type=Int32"), new("Output", "later=word")]),
            ("typed_index_assignment_converts_write_only", """
$array=@(1); [int]$array[0]='2'; "type=$($array[0].GetType().Name)"; $array[0]='word'; "later=$($array[0])"
""", [new("Output", "type=Int32"), new("Output", "later=word")]),
            ("typed_parenthesized_variable_target", """
([int]$value)='2'; ($value)='3'; "value=$value;type=$($value.GetType().Name)"
""", [new("Output", "value=3;type=Int32")]),
            ("typed_parenthesized_member_target", """
$object=@{Value=1}; ([int]$object.Value)='2'; "type=$($object.Value.GetType().Name)"; ($object.Value)='word'; "later=$($object.Value)"
""", [new("Output", "type=Int32"), new("Output", "later=word")]),
            ("typed_nested_tuple_assignment", """
$result=([int]$first,([int]$second,[int[]]$rest)='1','2','3','4'); "values=$first/$second/$($rest -join ',');rest=$($rest.GetType().Name);count=$($result.Count);first=$($result[0].GetType().Name);second=$($result[1].GetType().Name);nested=$($result[1].Count)"; $first='5'; $second='6'; $rest='7'; "later=$first/$second/$($rest.GetType().Name)"
""", [new("Output", "values=1/2/3,4;rest=Int32[];count=2;first=String;second=Object[];nested=3"), new("Output", "later=5/6/Int32[]")]),
            ("typed_parallel_remainder_expression_shape", """
$result=([int]$first,[int[]]$rest='1','2','3'); "count=$($result.Count);first=$($result[0].GetType().Name);second=$($result[1].GetType().Name);nested=$($result[1] -join ',');nestedcount=$($result[1].Count);stored=$($rest.GetType().Name)"
""", [new("Output", "count=2;first=String;second=Object[];nested=2,3;nestedcount=2;stored=Int32[]")]),
            ("typed_clear_optimized_bool_default", """
function Invoke-Example { [bool]$value=$true; try { Clear-Variable value -ErrorAction Stop; 'cleared' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "cleared"), new("Output", "value=False")]),
            ("typed_clear_unoptimized_bool_rejected", """
function Invoke-Example { . {}; [bool]$value=$true; try { Clear-Variable value -ErrorAction Stop; 'cleared' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "value=True")]),
            ("typed_callback_failed_binding_clears_constraint", """
[int]$value=7; try { 1 | ForEach-Object { param([byte]$value=300) 'body' } } catch { 'binding-rejected' }; "before=$value"; try { $value='bad'; 'assigned' } catch { 'constraint-kept' }; "after=$value;type=$($value.GetType().Name)"
""", [new("Output", "binding-rejected"), new("Output", "before=7"), new("Output", "assigned"), new("Output", "after=bad;type=String")]),
            ("typed_dot_failed_binding_preserves_constraint", """
[int]$value=7; try { . { param([byte]$value) 'body' } 300 } catch { 'binding-rejected' }; try { $value='bad'; 'assigned' } catch { 'constraint-kept' }; "value=$value;type=$($value.GetType().Name)"
""", [new("Output", "binding-rejected"), new("Output", "constraint-kept"), new("Output", "value=7;type=Int32")]),
            ("typed_optimizer_prior_increment_not_assignment", """
$value=0; function Invoke-Example { $value++; [bool]$value=$true; try { $value='false'; 'assigned' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "assigned"), new("Output", "value=True")]),
            ("typed_optimizer_increment_result_types", """
function Invoke-Example { [int]$number=1; "int-post=$(($number++).GetType().Name);int-pre=$((++$number).GetType().Name)"; [byte]$small=1; "byte-post=$(($small++).GetType().Name);byte-pre=$((++$small).GetType().Name);stored=$($small.GetType().Name)" }; Invoke-Example
""", [new("Output", "int-post=Int32;int-pre=Int32"), new("Output", "byte-post=Byte;byte-pre=Int32;stored=Byte")]),
            ("typed_optimizer_unreachable_declarations", """
function Invoke-Single { [bool]$value=$true; try { $value='false'; 'single:assigned' } catch { 'single:rejected' }; return; [string]$value='unreachable' }; function Invoke-Stacked { [bool]$value=$true; try { $value='false'; 'stacked:assigned' } catch { 'stacked:rejected' }; return; [string][int]$value=1 }; Invoke-Single; Invoke-Stacked
""", [new("Output", "single:assigned"), new("Output", "stacked:rejected")]),
            ("typed_optimizer_literal_call_command_deoptimizes", """
function Invoke-Example { [bool]$value=$true; & 'Set-Variable' dummy 1; try { $value='false'; 'assigned' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "value=True")]),
            ("typed_optimizer_dynamic_call_command_remains_optimized", """
function Invoke-Example { [bool]$value=$true; $command='Set-Variable'; & $command dummy 1; try { $value='false'; 'assigned' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "assigned"), new("Output", "value=True")]),
            ("typed_optimizer_unreachable_literal_call_deoptimizes", """
function Invoke-Example { [bool]$value=$true; if ($false) { & 'Set-Variable' dummy 1 }; try { $value='false'; 'assigned' } catch { 'rejected' }; "value=$value" }; Invoke-Example
""", [new("Output", "rejected"), new("Output", "value=True")]),
            ("typed_array_same_type_reassignment_preserves_alias", """
[int[]]$value=1,2; $alias=$value; $value=$alias; $alias[0]=9; "value=$($value -join ',');alias=$($alias -join ',');type=$($value.GetType().Name)"
""", [new("Output", "value=9,2;alias=9,2;type=Int32[]")]),
            ("typed_array_same_type_redeclaration_preserves_alias", """
[int[]]$value=1,2; $alias=$value; [int[]]$value=$alias; $alias[0]=9; "value=$($value -join ',');alias=$($alias -join ',');type=$($value.GetType().Name)"
""", [new("Output", "value=9,2;alias=9,2;type=Int32[]")]),
            ("typed_array_covariance_preserves_alias", """
$original=[string[]]@('one','two'); [object[]]$value=$original; $original[0]='changed'; "value=$($value -join ',');type=$($value.GetType().Name)"; $value[1]=42; "original=$($original -join ',');element=$($original[1].GetType().Name)"
""", [new("Output", "value=changed,two;type=String[]"), new("Output", "original=changed,42;element=String")]),
            ("typed_array_conversion_allocates_distinct_array", """
$original=[int[]]@(1,2); [object[]]$value=$original; $original[0]=9; "value=$($value -join ',');original=$($original -join ',');type=$($value.GetType().Name)"; $value[1]='word'; "later=$($value -join ',');original=$($original -join ',')"
""", [new("Output", "value=1,2;original=9,2;type=Object[]"), new("Output", "later=1,word;original=9,2")]),
            ("typed_arraylist_identity_and_null", """
[System.Collections.ArrayList]$value=1,2; $alias=$value; $value=$alias; [void]$alias.Add(3); "count=$($value.Count);values=$($value -join ',')"; $value=$null; "null=$($null -eq $value)"
""", [new("Output", "count=3;values=1,2,3"), new("Output", "null=True")]),
            ("typed_generic_list_identity_and_null", """
[System.Collections.Generic.List[int]]$value=1,2; $alias=$value; $value=$alias; $alias.Add(3); "count=$($value.Count);values=$($value -join ',')"; $value=$null; "null=$($null -eq $value)"
""", [new("Output", "count=3;values=1,2,3"), new("Output", "null=True")]),
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
        Console.WriteLine($"Typed variable scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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
