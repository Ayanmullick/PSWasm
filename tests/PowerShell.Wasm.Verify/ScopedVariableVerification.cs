using PSWasm;

internal static class ScopedVariableVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected records and malformed-variable rejection were independently recorded with native PowerShell 7.6.1.
        // Runtime errors use catch markers; parser rejection is checked separately from localized native messages.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, bool RejectsSyntax)[]
        {
            ("scope_root_aliases", """
$x=1; "$x/$local:x/$script:x/$global:x/$private:x"; $local:x=2; "$x/$local:x/$script:x/$global:x"
""", [new("Output", "1/1/1/1/1"), new("Output", "2/2/2/2")], false),
            ("scope_local_read_exact", """
$x='root'; & { "before=$x/$local:x/$private:x"; $local:x='child'; "after=$x/$local:x/$private:x/$global:x" }; $x
""", [new("Output", "before=root//"), new("Output", "after=child/child/child/root"), new("Output", "root")], false),
            ("scope_global_write", """
$x='root'; & { $global:x='changed'; "current=$x;root=$global:x" }; $x
""", [new("Output", "current=changed;root=changed"), new("Output", "changed")], false),
            ("scope_script_write", """
$script:x='root'; & { $script:x='changed' }; "$x/$script:x/$global:x"
""", [new("Output", "changed/changed/changed")], false),
            ("scope_nested_local_shadow", """
$x='root'; & { $x='parent'; & { "before=$x;local=$local:x"; $local:x='child'; "after=$x;root=$global:x" }; "parent=$x" }; "root=$x"
""", [new("Output", "before=parent;local="), new("Output", "after=child;root=root"), new("Output", "parent=parent"), new("Output", "root=root")], false),
            ("scope_local_case_insensitive", """
$x=1; & { $LOCAL:X=2; "$x/$LoCaL:x/$GLOBAL:X" }; $X
""", [new("Output", "2/2/1"), new("Output", "1")], false),
            ("scope_qualified_parallel_assignment", """
$x=1;$y=2; & { $local:x,$global:y=3,4; "$x/$global:x/$global:y" }; "$x/$y"
""", [new("Output", "3/1/4"), new("Output", "1/4")], false),
            ("scope_qualified_compound_assignment", """
$x=1; & { $global:x+=2; $local:x=10; $local:x*=2; "$x/$global:x" }; $x
""", [new("Output", "20/3"), new("Output", "3")], false),
            ("scope_qualified_increment", """
$x=1; & { $before=$global:x++; $after=++$global:x; "$before/$after/$global:x" }; $x
""", [new("Output", "1/3/3"), new("Output", "3")], false),
            ("scope_qualified_null_coalescing", """
$x=1; & { $local:x ??= 2; $global:x ??= 3; "$x/$global:x" }; $x
""", [new("Output", "2/1"), new("Output", "1")], false),
            ("scope_global_function_assignment", """
$x=1; function Change { $global:x+=2; $local:x=9; "$x/$global:x" }; Change; $x
""", [new("Output", "9/3"), new("Output", "3")], false),
            ("scope_script_nested_function_assignment", """
$x=1; function Outer { $x=2; function Inner { $script:x=3; "$x/$script:x/$global:x" }; Inner; $x }; Outer; $x
""", [new("Output", "2/3/3"), new("Output", "2"), new("Output", "3")], false),
            ("scope_dot_invocation_same_scope", """
$x=1; & { $x=2; . { $local:x=3; Set-Variable x 4 -Scope 0 }; "$x/$global:x" }; $x
""", [new("Output", "4/1"), new("Output", "1")], false),
            ("scope_global_splat", """
$global:opts=@{InputObject='hello'}; & { Write-Output @global:opts }
""", [new("Output", "hello")], false),
            ("scope_private_new_hidden", """
& { $private:x=1; & { "child=$x;local=$local:x;private=$private:x" }; "$x/$private:x" }
""", [new("Output", "child=;local=;private="), new("Output", "1/1")], false),
            ("scope_private_ancestor_fallback", """
$x='root'; & { $private:x='hidden'; "own=$x/$private:x"; & { "child=$x/$global:x"; & { "grandchild=$x" } } }
""", [new("Output", "own=hidden/hidden"), new("Output", "child=root/root"), new("Output", "grandchild=root")], false),
            ("scope_private_current_read_public", """
& { $x=1; "$private:x"; & { $x } }
""", [new("Output", "1"), new("Output", "1")], false),
            ("scope_private_assignment_existing_public", """
& { $x=1; $private:x=2; & { "child=$x" }; "$x/$private:x" }
""", [new("Output", "child=2"), new("Output", "2/2")], false),
            ("scope_private_flag_retained_write", """
& { $private:x=1; $x=2; $local:x=3; & { "child=$x" }; $x }
""", [new("Output", "child="), new("Output", "3")], false),
            ("scope_private_root_read_hidden", """
$private:x='root'; & { "plain=$x;global=$global:x;script=$script:x"; Get-Variable x -Scope Global -ValueOnly }
""", [new("Output", "plain=;global=;script="), new("Output", "root")], false),
            ("scope_private_root_write_replaces", """
[int]$private:x=1; & { $global:x='word'; "plain=$x;global=$global:x" }; "$x/$($x -is [string])"
""", [new("Output", "plain=word;global=word"), new("Output", "word/True")], false),
            ("scope_private_root_same_scope_write", """
$private:x=1; $global:x=2; & { "plain=$x;global=$global:x" }; $x
""", [new("Output", "plain=;global="), new("Output", "2")], false),
            ("scope_private_ancestor_shadow", """
$x='root'; & { $private:x='hidden'; & { $x='child'; "$x/$global:x" }; $x }; $x
""", [new("Output", "child/root"), new("Output", "hidden"), new("Output", "root")], false),
            ("scope_typed_global_reassign", """
[int]$x=1; & { $global:x='3'; $local:x='word'; "$global:x/$local:x"; try {$global:x='bad'} catch {'rejected'} }; "$x/$($x -is [int])"
""", [new("Output", "3/word"), new("Output", "rejected"), new("Output", "3/True")], false),
            ("scope_typed_local_alias", """
& { [int]$local:x='1'; $x='2'; "$x/$local:x/$($x -is [int])"; ($local:x='3') -is [int] }
""", [new("Output", "2/2/True"), new("Output", "True")], false),
            ("scope_typed_local_then_plain", """
& { [int]$x=1; $local:x='2'; "$x/$local:x/$($local:x -is [int])"; try {$local:x='bad'} catch {'rejected'}; $x }
""", [new("Output", "2/2/True"), new("Output", "rejected"), new("Output", "2")], false),
            ("scope_typed_global_assignment_result", """
[int]$x=1; & { ($global:x='2') -is [string]; $global:x -is [int] }
""", [new("Output", "True"), new("Output", "True")], false),
            ("scope_typed_private_deopt", """
& { [bool]$private:x=$false; try {$x='text'} catch {'rejected'}; "$x/$($x -is [bool])"; & { "child=$x" } }
""", [new("Output", "rejected"), new("Output", "False/True"), new("Output", "child=")], false),
            ("scope_typed_global_failed_redeclare", """
[int]$x=1; & { try { [byte]$global:x=300 } catch {'rejected'}; $global:x=256 }; "$x/$($x -is [int])"
""", [new("Output", "rejected"), new("Output", "256/True")], false),
            ("scope_typed_local_redeclaration", """
$x=1; & { [int]$local:x=2; [string]$local:x='text'; $local:x=3; "$x/$($x -is [string])" }; $x
""", [new("Output", "3/True"), new("Output", "1")], false),
            ("scope_get_all_visible", """
$scopeA=1; $scopeB=2; & { $scopeA=3; Get-Variable scopeA,scopeB -ValueOnly }
""", [new("Output", "3"), new("Output", "2")], false),
            ("scope_get_exact_local", """
$x=1; & { try {Get-Variable x -Scope Local -ValueOnly -ErrorAction Stop} catch {'absent'}; Get-Variable x -ValueOnly }
""", [new("Output", "absent"), new("Output", "1")], false),
            ("scope_get_exact_numeric", """
$x=1; & { & { try {Get-Variable x -Scope 1 -ValueOnly -ErrorAction Stop} catch {'parent-absent'}; Get-Variable x -Scope 2 -ValueOnly } }
""", [new("Output", "parent-absent"), new("Output", "1")], false),
            ("scope_get_named_root", """
$x=1; & { $x=2; Get-Variable x -Scope Global -ValueOnly; Get-Variable x -Scope Script -ValueOnly; Get-Variable x -Scope Local -ValueOnly }
""", [new("Output", "1"), new("Output", "1"), new("Output", "2")], false),
            ("scope_get_private_is_local", """
& { $scopeA=1;$private:scopeB=2; Get-Variable scopeA,scopeB -Scope Private -ValueOnly }
""", [new("Output", "1"), new("Output", "2")], false),
            ("scope_get_skips_private_ancestors", """
$scopeA='root';$scopeB='root-b'; & { $private:scopeA='private'; $scopeB='child-b'; & { Get-Variable scopeA,scopeB -ValueOnly } }
""", [new("Output", "root"), new("Output", "child-b")], false),
            ("scope_get_explicit_parent_private", """
& { $private:x=1; & { Get-Variable x -Scope 1 -ValueOnly } }
""", [new("Output", "1")], false),
            ("scope_get_wildcard_exact", """
$scopeA=1; & { $scopeB=2; Get-Variable scope* -Scope 0 -ValueOnly; Get-Variable scope* -Scope 1 -ValueOnly }
""", [new("Output", "2"), new("Output", "1")], false),
            ("scope_get_multiple_names_order", """
$scopeA=1; $scopeB=2; & { Get-Variable scopeB,scopeA -Scope Global -ValueOnly }
""", [new("Output", "2"), new("Output", "1")], false),
            ("scope_set_numeric_parent", """
$x='root'; & { $x='parent'; & { Set-Variable x changed -Scope 1; $x }; $x }; $x
""", [new("Output", "changed"), new("Output", "changed"), new("Output", "root")], false),
            ("scope_set_numeric_root", """
$x=1; & { & { Set-Variable x 3 -Scope 2 } }; $x
""", [new("Output", "3")], false),
            ("scope_set_named_root", """
$x=1; & { Set-Variable x 2 -Scope Global; Set-Variable x 3 -Scope Script; Set-Variable x 4 -Scope Local; "$x/$global:x" }; $x
""", [new("Output", "4/3"), new("Output", "3")], false),
            ("scope_set_scope_private_new", """
& { Set-Variable x 1 -Scope Private; & { "child=$x" }; $x }
""", [new("Output", "child="), new("Output", "1")], false),
            ("scope_set_scope_private_existing", """
& { $x=1; Set-Variable x 2 -Scope Private; & { "child=$x" }; $x }
""", [new("Output", "child=2"), new("Output", "2")], false),
            ("scope_set_explicit_private_parent", """
$private:x=1; & { Set-Variable x 2 -Scope Global; "child=$x;global=$global:x" }; $x
""", [new("Output", "child=;global="), new("Output", "2")], false),
            ("scope_set_typed_parent", """
[int]$x=1; & { Set-Variable x '2' -Scope 1; try {Set-Variable x 'bad' -Scope 1 -ErrorAction Stop} catch {'rejected'} }; "$x/$($x -is [int])"
""", [new("Output", "rejected"), new("Output", "2/True")], false),
            ("scope_set_qualified_name", """
$x=1; & { $x=2; Set-Variable 'global:x' 3; "$x/$global:x" }; $x
""", [new("Output", "2/3"), new("Output", "3")], false),
            ("scope_set_qualified_name_explicit_scope_literal", """
$x=1; & { $x=2; Set-Variable 'global:x' 3 -Scope Local; "$x/$global:x"; Get-Variable 'global:x' -Scope Local -ValueOnly }; $x
""", [new("Output", "2/1"), new("Output", "3"), new("Output", "1")], false),
            ("scope_set_qualified_local_explicit_global_literal", """
$x=1; & { Set-Variable 'local:x' 3 -Scope Global }; Get-Variable 'local:x' -Scope Global -ValueOnly; $x
""", [new("Output", "3"), new("Output", "1")], false),
            ("scope_set_private_name_marks_existing", """
& { $x=1; Set-Variable 'private:x' 2; & { "child=$x" }; $x }
""", [new("Output", "child="), new("Output", "2")], false),
            ("scope_set_private_name_replaces_ancestor", """
$x=1; & { Set-Variable 'private:x' 2; & { "child=$x" }; $x }; $x
""", [new("Output", "child=1"), new("Output", "2"), new("Output", "1")], false),
            ("scope_set_wildcard_existing", """
$scopeA=1;$scopeB=2; & { Set-Variable 'scope*' 3 -Scope Global }; "$scopeA/$scopeB"
""", [new("Output", "3/3")], false),
            ("scope_set_allows_new_selected_scope", """
& { Set-Variable x 4 -Scope 1 }; "$x/$global:x"
""", [new("Output", "4/4")], false),
            ("scope_clear_default_inherited", """
[int]$x=2; & { Clear-Variable x; "null=$($null -eq $x);int=$($x -is [int])"; $x='word'; $x }; $x
""", [new("Output", "null=True;int=False"), new("Output", "word"), new("Output", "2")], false),
            ("scope_clear_explicit_parent_typed", """
[int]$x=2; & { Clear-Variable x -Scope 1 }; "$x/$($x -is [int])"
""", [new("Output", "0/True")], false),
            ("scope_clear_named_global", """
$x=1; & { $x=2; Clear-Variable x -Scope Global; "child=$x;global=$global:x" }; "root=$x"
""", [new("Output", "child=2;global="), new("Output", "root=")], false),
            ("scope_clear_scope_private_exact", """
$x=1; & { try {Clear-Variable x -Scope Private -ErrorAction Stop} catch {'absent'}; $x }; $x
""", [new("Output", "absent"), new("Output", "1"), new("Output", "1")], false),
            ("scope_clear_wildcard_parent", """
$scopeA=1;$scopeB=2; & { Clear-Variable 'scope*' -Scope 1 }; "a=$scopeA;b=$scopeB"
""", [new("Output", "a=;b=")], false),
            ("scope_clear_qualified_name_literal", """
$x=1; try {Clear-Variable 'global:x' -ErrorAction Stop} catch {'literal-absent'}; $x
""", [new("Output", "literal-absent"), new("Output", "1")], false),
            ("scope_clear_private_preserves", """
$private:x=1; & { Clear-Variable x -Scope Global; "child=$x" }; "root=$x"; & { try {Get-Variable x -ErrorAction Stop} catch {'hidden'} }
""", [new("Output", "child="), new("Output", "root="), new("Output", "hidden")], false),
            ("scope_remove_default_current", """
$x=1; & { $x=2; Remove-Variable x; "$x/$local:x" }; $x
""", [new("Output", "1/"), new("Output", "1")], false),
            ("scope_remove_default_does_not_remove_ancestor", """
$x=1; & { try {Remove-Variable x -ErrorAction Stop} catch {'absent'}; $x }; $x
""", [new("Output", "absent"), new("Output", "1"), new("Output", "1")], false),
            ("scope_remove_explicit_parent", """
$x=1; & { $x=2; Remove-Variable x -Scope 1; "$x/$global:x" }; "root=$x"
""", [new("Output", "2/"), new("Output", "root=")], false),
            ("scope_remove_typed_constraint", """
[int]$x=1; & { Remove-Variable x -Scope Global; Set-Variable x word -Scope Global }; "$x/$($x -is [string])"
""", [new("Output", "word/True")], false),
            ("scope_remove_private_parent", """
$private:x=1; & { Remove-Variable x -Scope Global; $global:x=2 }; & { $x }; $x
""", [new("Output", "2"), new("Output", "2")], false),
            ("scope_remove_qualified_name_literal", """
$x=1; try {Remove-Variable 'global:x' -ErrorAction Stop} catch {'literal-absent'}; $x
""", [new("Output", "literal-absent"), new("Output", "1")], false),
            ("scope_remove_wildcard_parent", """
$scopeA=1;$scopeB=2; & { Remove-Variable scope* -Scope 1 }; "a=$scopeA;b=$scopeB"
""", [new("Output", "a=;b=")], false),
            ("scope_get_qualified_name_literal", """
$x=1; try {Get-Variable 'global:x' -ValueOnly -ErrorAction Stop} catch {'literal-absent'}; Get-Variable x -Scope Global -ValueOnly
""", [new("Output", "literal-absent"), new("Output", "1")], false),
            ("scope_literal_colon_clear", """
Set-Variable 'global:x' 1 -Scope Local; Clear-Variable 'global:x' -Scope Local; "cleared=$((Get-Variable 'global:x' -Scope Local).Value)"
""", [new("Output", "cleared=")], false),
            ("scope_invalid_get", """
$x=1; try { Get-Variable x -Scope -1 -ErrorAction Stop } catch {'negative'}; try { Get-Variable x -Scope 9 -ErrorAction Stop } catch {'too-deep'}; try { Get-Variable x -Scope invalid -ErrorAction Stop } catch {'invalid'}; $x
""", [new("Output", "negative"), new("Output", "too-deep"), new("Output", "invalid"), new("Output", "1")], false),
            ("scope_invalid_set", """
$x=1; try { Set-Variable x 2 -Scope -1 -ErrorAction Stop } catch {'negative'}; try { Set-Variable x 2 -Scope 9 -ErrorAction Stop } catch {'too-deep'}; try { Set-Variable x 2 -Scope invalid -ErrorAction Stop } catch {'invalid'}; $x
""", [new("Output", "negative"), new("Output", "too-deep"), new("Output", "invalid"), new("Output", "1")], false),
            ("scope_invalid_clear", """
$x=1; try { Clear-Variable x -Scope -1 -ErrorAction Stop } catch {'negative'}; try { Clear-Variable x -Scope 9 -ErrorAction Stop } catch {'too-deep'}; try { Clear-Variable x -Scope invalid -ErrorAction Stop } catch {'invalid'}; $x
""", [new("Output", "negative"), new("Output", "too-deep"), new("Output", "invalid"), new("Output", "1")], false),
            ("scope_invalid_remove", """
$x=1; try { Remove-Variable x -Scope -1 -ErrorAction Stop } catch {'negative'}; try { Remove-Variable x -Scope 9 -ErrorAction Stop } catch {'too-deep'}; try { Remove-Variable x -Scope invalid -ErrorAction Stop } catch {'invalid'}; $x
""", [new("Output", "negative"), new("Output", "too-deep"), new("Output", "invalid"), new("Output", "1")], false),
            ("scope_numeric_string", """
$x=1; & { Get-Variable x -Scope '1' -ValueOnly; Set-Variable x 2 -Scope '1' }; $x
""", [new("Output", "1"), new("Output", "2")], false),
            ("scope_callback_local_write", """
$x=1; 1 | ForEach-Object { $local:x=2; "$x/$local:x/$global:x" }; $x
""", [new("Output", "2/2/2"), new("Output", "2")], false),
            ("scope_callback_numeric_parent", """
$x=1; & { $x=2; 1 | ForEach-Object { Set-Variable x 3 -Scope 1; "$x/$global:x" }; $x }; $x
""", [new("Output", "2/3"), new("Output", "2"), new("Output", "3")], false),
            ("scope_callback_current_item", """
1,2 | ForEach-Object { "$_/$local:_/$local:PSItem/$private:PSItem"; Get-Variable PSItem -Scope 0 -ValueOnly }
""", [new("Output", "1/1/1/1"), new("Output", "1"), new("Output", "2/2/2/2"), new("Output", "2")], false),
            ("scope_calculated_child_parent", """
$x=1; 2 | Select-Object @{Name='Value';Expression={Set-Variable x 3 -Scope 1; "$local:x/$global:x"}} | ForEach-Object {$_.Value}; $x
""", [new("Output", "/3"), new("Output", "3")], false),
            ("scope_private_callback_same_scope", """
& { $private:x=1; 2 | ForEach-Object { "$x/$local:x/$private:x"; $x=3 }; & { "nested=$x" }; $x }
""", [new("Output", "1/1/1"), new("Output", "nested="), new("Output", "3")], false),
            ("scope_qualified_alias", """
$global:PSItem=2; & { "$global:_/$global:PSItem"; $local:PSItem=3; "$_/$local:_/$local:PSItem" }
""", [new("Output", "2/2"), new("Output", "3/3/3")], false),
            ("scope_qualified_error", """
try {throw 'boom'} catch {}; & { "plain=$($Error.Count);local=$($local:Error.Count);global=$($global:Error.Count);script=$($script:Error.Count);private=$($private:Error.Count)" }
""", [new("Output", "plain=1;local=0;global=1;script=1;private=0")], false),
            ("scope_qualified_question", """
1; & { "plain=$?;local=${local:?};global=${global:?};private=${private:?};script=${script:?}"; try {${global:?}=$false} catch {'readonly'} }
""", [new("Output", "1"), new("Output", "plain=True;local=True;global=True;private=True;script=True"), new("Output", "readonly")], false),
            ("scope_qualified_constants", """
& { "true=$true;local=$local:true;global=$global:true;null=$($null -eq $local:null)"; try {$local:true=5} catch {'readonly-local'}; try {$global:false=5} catch {'readonly-global'} }
""", [new("Output", "true=True;local=True;global=True;null=True"), new("Output", "readonly-local"), new("Output", "readonly-global")], false),
            ("scope_function_parameter_local", """
$x='root'; function Read-X { param($x) "$x/$local:x/$global:x"; & { "$x/$local:x/$global:x" } }; Read-X 'parameter'; $x
""", [new("Output", "parameter/parameter/root"), new("Output", "parameter//root"), new("Output", "root")], false),
            ("scope_dot_parameter_local", """
$x='root'; & { $x='child'; . {param($x) "$local:x/$global:x"} 'parameter'; $x }; $x
""", [new("Output", "parameter/root"), new("Output", "parameter"), new("Output", "root")], false),
            ("scope_pipeline_qualified_splat", """
$global:options=@{InputObject='text'}; & { Write-Output @global:options }
""", [new("Output", "text")], false),
            ("scope_null_empty_get", """
$x=1; try {Get-Variable x -Scope $null -ErrorAction Stop} catch {'null'}; try {Get-Variable x -Scope '' -ErrorAction Stop} catch {'empty'}; $x
""", [new("Output", "null"), new("Output", "empty"), new("Output", "1")], false),
            ("scope_null_empty_set", """
$x=1; try {Set-Variable x 2 -Scope $null -ErrorAction Stop} catch {'null'}; try {Set-Variable x 2 -Scope '' -ErrorAction Stop} catch {'empty'}; $x
""", [new("Output", "null"), new("Output", "empty"), new("Output", "1")], false),
            ("scope_null_empty_clear", """
$x=1; try {Clear-Variable x -Scope $null -ErrorAction Stop} catch {'null'}; try {Clear-Variable x -Scope '' -ErrorAction Stop} catch {'empty'}; $x
""", [new("Output", "null"), new("Output", "empty"), new("Output", "1")], false),
            ("scope_null_empty_remove", """
$x=1; try {Remove-Variable x -Scope $null -ErrorAction Stop} catch {'null'}; try {Remove-Variable x -Scope '' -ErrorAction Stop} catch {'empty'}; $x
""", [new("Output", "null"), new("Output", "empty"), new("Output", "1")], false),
            ("scope_get_missing_literal", """
try {Get-Variable noSuchScopeVariable -ErrorAction Stop} catch {'absent'}; 'after'
""", [new("Output", "absent"), new("Output", "after")], false),
            ("scope_clear_missing_literal", """
try {Clear-Variable noSuchScopeVariable -ErrorAction Stop} catch {'absent'}; "unset=$($null -eq $noSuchScopeVariable)"
""", [new("Output", "absent"), new("Output", "unset=True")], false),
            ("scope_remove_missing_literal", """
try {Remove-Variable noSuchScopeVariable -ErrorAction Stop} catch {'absent'}; 'after'
""", [new("Output", "absent"), new("Output", "after")], false),
            ("scope_missing_wildcard_silent", """
Get-Variable noSuchScopeVariable*; Clear-Variable noSuchScopeVariable*; Remove-Variable noSuchScopeVariable*; 'after'
""", [new("Output", "after")], false),
            ("scope_braced_assignment", """
${local:x}=1; & { ${local:x}=2; "${local:x}/${global:x}"; ${local:x} }; $x
""", [new("Output", "2/1"), new("Output", "2"), new("Output", "1")], false),
            ("scope_braced_spaced_name", """
${global:some value}='root'; & { ${local:some value}='child'; "${local:some value}/${global:some value}"; ${local:some value} }
""", [new("Output", "child/root"), new("Output", "child")], false),
            ("scope_braced_escaped_closing", """
${global:brace`}}='value'; "${global:brace`}}"; ${global:brace`}}
""", [new("Output", "value"), new("Output", "value")], false),
            ("scope_braced_escaped_opening", """
${global:brace`{}='value'; "${global:brace`{}"; ${global:brace`{}
""", [new("Output", "value"), new("Output", "value")], false),
            ("scope_digit_variable_name", """
$global:1='root'; & { $local:1='child'; "$local:1/$global:1"; $local:1 }
""", [new("Output", "child/root"), new("Output", "child")], false),
            ("scope_dollar_escaped", """
$global:x='value'; "`$global:x"; "`${global:x}"; "$global:x"
""", [new("Output", "$global:x"), new("Output", "${global:x}"), new("Output", "value")], false),
            ("scope_question_variable_name", """
$global:value?='question'; "$global:value?"; $global:value?; ${global:value?}
""", [new("Output", "question"), new("Output", "question"), new("Output", "question")], false),
            ("scope_question_unbraced", """
$global:?; & { "$local:?/$global:?"; try {$local:?=$false} catch {'readonly'} }
""", [new("Output", "True"), new("Output", "True/True"), new("Output", "readonly")], false),
            ("scope_null_conditional_requires_braces", """
$value=@{Name='plain'}; $value?=@{Name='question'}; $value?.Name; ${value}?.Name; ${missing}?.Name ?? 'missing'
""", [new("Output", "question"), new("Output", "plain"), new("Output", "missing")], false),
            ("scope_empty_braced", """
${}
""", [], true),
            ("scope_empty_qualified_braced", """
${local:}
""", [], true),
            ("scope_empty_qualified", """
$local:
""", [], true),
            ("scope_empty_braced_string", """
"${}"
""", [], true),
            ("scope_empty_qualified_string", """
"$local:"
""", [], true),
            ("scope_unclosed_braced", """
${local:x
""", [], true),
            ("scope_unclosed_braced_string", """
"${local:x"
""", [], true),
            ("scope_null_assignment_discard", """
$null=1; $local:null=2; $global:null=3; "$($null -eq $null)/$($null -eq $local:null)/$($null -eq $global:null)"
""", [new("Output", "True/True/True")], false),
            ("scope_null_cmdlet_set_discard", """
try {Set-Variable null 1 -ErrorAction Stop; 'set'} catch {'rejected'}; try {Set-Variable null 2 -Scope Global -ErrorAction Stop; 'set-global'} catch {'rejected-global'}; "$($null -eq $null)"
""", [new("Output", "set"), new("Output", "set-global"), new("Output", "True")], false),
            ("scope_constant_cmdlet_readonly", """
try {Set-Variable true $false -ErrorAction Stop} catch {'true-readonly'}; try {Set-Variable false $true -Scope Local -ErrorAction Stop} catch {'false-readonly'}; "$true/$false"
""", [new("Output", "true-readonly"), new("Output", "false-readonly"), new("Output", "True/False")], false),
            ("scope_escaped_variable_name_suffix", """
$x='plain'; ${x?}='question'; "$x`?/$x?"
""", [new("Output", "plain?/question")], false),
            ("scope_clear_default_literal_qualified_collision", """
$global:scopeLiteral=9; Set-Variable 'global:scopeLiteral' 7 -Scope Local; Clear-Variable 'global:scopeLiteral'; "globalNull=$($null -eq $global:scopeLiteral);literal=$(Get-Variable 'global:scopeLiteral' -ValueOnly)"
""", [new("Output", "globalNull=True;literal=7")], false),
            ("scope_clear_explicit_literal_qualified_collision", """
$global:scopeLiteral=9; Set-Variable 'global:scopeLiteral' 7 -Scope Local; Clear-Variable 'global:scopeLiteral' -Scope Local; "global=$global:scopeLiteral;literalNull=$($null -eq (Get-Variable 'global:scopeLiteral' -ValueOnly))"
""", [new("Output", "global=9;literalNull=True")], false),
            ("scope_remove_literal_environment_collision", """
$env:PSWasmScopeAudit='environment'; Set-Variable 'env:PSWasmScopeAudit' variable -Scope Local; Remove-Variable 'env:PSWasmScopeAudit'; try { Get-Variable 'env:PSWasmScopeAudit' -ErrorAction Stop } catch {'literal-removed'}; $env:PSWasmScopeAudit
""", [new("Output", "literal-removed"), new("Output", "environment")], false),
            ("scope_clear_default_ordinary_ancestor", """
$scopeValue='ancestor'; & { Clear-Variable scopeValue; "localNull=$($null -eq $local:scopeValue);value=[$scopeValue]" }; $scopeValue
""", [new("Output", "localNull=True;value=[]"), new("Output", "ancestor")], false),
            ("scope_set_qualified_replaces_constraint_local", """
[int]$x=1; try {Set-Variable 'local:x' bad -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "set"), new("Output", "bad/False"), new("Output", "child=bad")], false),
            ("scope_set_explicit_preserves_constraint_local", """
[int]$x=1; try {Set-Variable x bad -Scope local -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "rejected"), new("Output", "1/True"), new("Output", "child=1")], false),
            ("scope_set_qualified_replaces_constraint_global", """
[int]$x=1; try {Set-Variable 'global:x' bad -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "set"), new("Output", "bad/False"), new("Output", "child=bad")], false),
            ("scope_set_explicit_preserves_constraint_global", """
[int]$x=1; try {Set-Variable x bad -Scope global -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "rejected"), new("Output", "1/True"), new("Output", "child=1")], false),
            ("scope_set_qualified_replaces_constraint_script", """
[int]$x=1; try {Set-Variable 'script:x' bad -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "set"), new("Output", "bad/False"), new("Output", "child=bad")], false),
            ("scope_set_explicit_preserves_constraint_script", """
[int]$x=1; try {Set-Variable x bad -Scope script -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "rejected"), new("Output", "1/True"), new("Output", "child=1")], false),
            ("scope_set_qualified_replaces_constraint_private", """
[int]$x=1; try {Set-Variable 'private:x' bad -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "set"), new("Output", "bad/False"), new("Output", "child=")], false),
            ("scope_set_explicit_preserves_constraint_private", """
[int]$x=1; try {Set-Variable x bad -Scope private -ErrorAction Stop; 'set'} catch {'rejected'}; "$x/$($x -is [int])"; & {"child=$x"}
""", [new("Output", "rejected"), new("Output", "1/True"), new("Output", "child=1")], false),
            ("scope_set_qualified_global_replaces_private", """
$private:x=1; Set-Variable 'global:x' 2; & {"child=$x;global=$global:x"}; $x
""", [new("Output", "child=2;global=2"), new("Output", "2")], false),
            ("scope_set_qualified_local_replaces_private", """
& {$private:x=1; Set-Variable 'local:x' 2; & {"child=$x"}; $x}
""", [new("Output", "child=2"), new("Output", "2")], false),
            ("scope_set_ordinary_preserves_private", """
$private:x=1; Set-Variable x 2; & {"child=$x;global=$global:x"}; $x
""", [new("Output", "child=;global="), new("Output", "2")], false),
            ("scope_remove_null_rejected", """
try {Remove-Variable null -ErrorAction Stop; 'removed'} catch {'readonly'}; try {Remove-Variable null -Scope Global -ErrorAction Stop; 'removed-global'} catch {'readonly-global'}; $null -eq $null
""", [new("Output", "readonly"), new("Output", "readonly-global"), new("Output", "True")], false),
            ("scope_clear_null_succeeds", """
try {Clear-Variable null -ErrorAction Stop; 'cleared'} catch {'rejected'}; try {Clear-Variable null -Scope Global -ErrorAction Stop; 'cleared-global'} catch {'rejected-global'}; $null -eq $null
""", [new("Output", "cleared"), new("Output", "cleared-global"), new("Output", "True")], false),
            ("scope_parameter_global_function_new", """
function Scope-Test {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; Scope-Test; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "typed-after")], false),
            ("scope_parameter_global_ampersand_new", """
& {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "typed-after")], false),
            ("scope_parameter_global_dot_new", """
. {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "typed-after")], false),
            ("scope_parameter_global_foreach_new", """
1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after="), new("Output", "untyped-after")], false),
            ("scope_parameter_global_collection_new", """
(1).ForEach({param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}); "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after="), new("Output", "untyped-after")], false),
            ("scope_parameter_global_calculated_new", """
1 | Select-Object @{Name='Value';Expression={param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}} | ForEach-Object {$_.Value}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "typed-after")], false),
            ("scope_parameter_global_function_existing", """
$scopeP='old'; function Scope-Test {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; Scope-Test; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "untyped-during"), new("Output", "after=5"), new("Output", "untyped-after")], false),
            ("scope_parameter_global_ampersand_existing", """
$scopeP='old'; & {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "untyped-during"), new("Output", "after=5"), new("Output", "untyped-after")], false),
            ("scope_parameter_global_dot_existing", """
$scopeP='old'; . {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "untyped-during"), new("Output", "after=5"), new("Output", "untyped-after")], false),
            ("scope_parameter_global_foreach_existing", """
$scopeP='old'; 1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "untyped-after")], false),
            ("scope_parameter_global_collection_existing", """
$scopeP='old'; (1).ForEach({param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}); "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "untyped-after")], false),
            ("scope_parameter_global_calculated_existing", """
$scopeP='old'; 1 | Select-Object @{Name='Value';Expression={param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; try {$global:scopeP='bad';'untyped-during'} catch {'typed-during'}; $global:scopeP=5}} | ForEach-Object {$_.Value}; "after=$scopeP"; try {$scopeP='after';'untyped-after'} catch {'typed-after'}
""", [new("Output", "during=5/5"), new("Output", "typed-during"), new("Output", "after=5"), new("Output", "typed-after")], false),
            ("scope_parameter_function_bound_key", """
$x='root'; function Scope-Test {param($global:x) "$x/$global:x/$($PSBoundParameters['global:x'])"}; Scope-Test arg; $x
""", [new("Output", "arg/arg/arg"), new("Output", "arg")], false),
            ("scope_parameter_private_function", """
$x='root'; function Scope-Test {param($private:x) "$x/$private:x/$($PSBoundParameters['private:x'])"; & {"child=$x"}}; Scope-Test arg; $x
""", [new("Output", "arg/arg/arg"), new("Output", "child=root"), new("Output", "root")], false),
            ("scope_parameter_local_function", """
$x='root'; function Scope-Test {param($local:x) "$x/$local:x/$($PSBoundParameters['local:x'])"; & {"child=$x"}}; Scope-Test arg; $x
""", [new("Output", "arg/arg/arg"), new("Output", "child=arg"), new("Output", "root")], false),
            ("scope_parameter_ampersand_default_order", """
$x='root';$y='root-y'; & {param($global:x='first',$global:y=$global:x) "$x/$y/$($PSBoundParameters.Count)"}; "after=$x/$y"
""", [new("Output", "first/first/0"), new("Output", "after=first/first")], false),
            ("scope_parameter_dot_default_order", """
$x='root';$y='root-y'; . {param($global:x='first',$global:y=$global:x) "$x/$y/$($PSBoundParameters.Count)"}; "after=$x/$y"
""", [new("Output", "first/first/0"), new("Output", "after=first/first")], false),
            ("scope_parameter_foreach_default_order", """
$x='root';$y='root-y'; 1 | ForEach-Object {param($global:x='first',$global:y=$global:x) "$x/$y/$($PSBoundParameters.Count)"}; "after=$x/$y"
""", [new("Output", "first/first/0"), new("Output", "after=first/first")], false),
            ("scope_parameter_ampersand_partial_failure", """
$x='root';[int]$y=1; try {& {param($global:x='first',[int]$global:y='bad');'body'}} catch {'rejected'}; "after=$x/$y"; try {$y='word';'untyped'} catch {'typed'}
""", [new("Output", "rejected"), new("Output", "after=first/1"), new("Output", "typed")], false),
            ("scope_parameter_dot_partial_failure", """
$x='root';[int]$y=1; try {. {param($global:x='first',[int]$global:y='bad');'body'}} catch {'rejected'}; "after=$x/$y"; try {$y='word';'untyped'} catch {'typed'}
""", [new("Output", "rejected"), new("Output", "after=first/1"), new("Output", "typed")], false),
            ("scope_parameter_foreach_partial_failure", """
$x='root';[int]$y=1; try {1 | ForEach-Object {param($global:x='first',[int]$global:y='bad');'body'}} catch {'rejected'}; "after=$x/$y"; try {$y='word';'untyped'} catch {'typed'}
""", [new("Output", "rejected"), new("Output", "after=first/1"), new("Output", "untyped")], false),
            ("scope_parameter_private_foreach", """
$x='root'; 1 | ForEach-Object {param($private:x='private') "$x/$private:x"; & {"child=$x"}}; "after=$x"
""", [new("Output", "private/private"), new("Output", "child="), new("Output", "after=private")], false),
            ("scope_parameter_private_collection", """
$x='root'; (1).ForEach({param($private:x='private') "$x/$private:x"; & {"child=$x"}}); "after=$x"
""", [new("Output", "private/private"), new("Output", "child="), new("Output", "after=private")], false),
            ("scope_parameter_private_calculated", """
$x='root'; 1 | Select-Object @{Name='Value';Expression={param($private:x='private') "$x/$private:x"; & {"child=$x"}}} | ForEach-Object {$_.Value}; "after=$x"
""", [new("Output", "private/private"), new("Output", "child=root"), new("Output", "after=root")], false),
            ("scope_parameter_function_root_private_replaced", """
$private:scopeP='old'; function F {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"}; F; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_nested_callback_existing_global_removed", """
$scopeP='old'; function F {1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"}; "end=$global:scopeP"}; F; "after=$scopeP"; try {$scopeP='after';'untyped'} catch {'typed'}
""", [new("Output", "during=5/5"), new("Output", "end="), new("Output", "after="), new("Output", "untyped")], false),
            ("scope_parameter_nested_callback_new_global_removed", """
function F {1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"}; "end=$global:scopeP"}; F; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "end="), new("Output", "after=")], false),
            ("scope_parameter_nested_local_dynamic_callback", """
function F {Set-Variable scopeP old; 1 | ForEach-Object {param([int]$local:scopeP=5) "during=$scopeP/$local:scopeP"}; "end=$scopeP"; try {$scopeP='after';'untyped'} catch {'typed'}}; F
""", [new("Output", "during=5/5"), new("Output", "end=5"), new("Output", "untyped")], false),
            ("scope_parameter_nested_callback_global_binding_failure", """
$scopeP='old'; function F {try {1 | ForEach-Object {param([int]$global:scopeP='bad');'body'}} catch {'rejected'}}; F; "after=$scopeP"
""", [new("Output", "rejected"), new("Output", "after=")], false),
            ("scope_parameter_script_default", """
$x='root'; function Scope-Test {param([int]$script:x=3) "$x/$script:x"}; Scope-Test; "after=$x"
""", [new("Output", "3/3"), new("Output", "after=3")], false),
            ("scope_parameter_braced_name", """
function Scope-Test {param(${global:spaced name}) "${global:spaced name}"; $PSBoundParameters['global:spaced name']}; Scope-Test bound; "after=${global:spaced name}"
""", [new("Output", "bound"), new("Output", "bound"), new("Output", "after=bound")], false),
            ("scope_parameter_local_sequential_default", """
$x='root'; function Scope-Test {param($local:x='local',$y=$local:x) "$x/$y/$global:x"}; Scope-Test; $x
""", [new("Output", "local/local/root"), new("Output", "root")], false),
            ("scope_parameter_callback_distinct_local_global_targets", """
$global:scopeP='root'; function F {Set-Variable scopeP local; 1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"}; "local=$scopeP;global=$global:scopeP"; try {$global:scopeP='word';'open'} catch {'typed'}}; F; "after=$global:scopeP"
""", [new("Output", "during=local/5"), new("Output", "local=local;global=5"), new("Output", "typed"), new("Output", "after=5")], false),
            ("scope_parameter_function_existing_private_global_untyped", """
$private:scopeP='old'; function F {param([int]$global:scopeP=5) try {$global:scopeP='word';'open'} catch {'typed'}}; F; "after=$scopeP"
""", [new("Output", "open"), new("Output", "after=word")], false),
            ("scope_parameter_private_root_visibility_ampersand", """
$private:scopeP='old'; & {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_private_root_visibility_dot", """
$private:scopeP='old'; . {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_private_root_visibility_foreach", """
$private:scopeP='old'; 1 | ForEach-Object {param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_private_root_visibility_collection", """
$private:scopeP='old'; (1).ForEach({param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; & {"inner=$scopeP"}}); & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_private_root_visibility_calculated", """
$private:scopeP='old'; 1 | Select-Object @{Name='Value';Expression={param([int]$global:scopeP=5) "during=$scopeP/$global:scopeP"; & {"inner=$scopeP"}}} | ForEach-Object {$_.Value}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5/5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_existing_private_dot_unqualified", """
$private:scopeP='old'; . {param([int]$scopeP=5) "during=$scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_existing_private_foreach_unqualified", """
$private:scopeP='old'; 1 | ForEach-Object {param([int]$scopeP=5) "during=$scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_existing_private_dot_local", """
$private:scopeP='old'; . {param([int]$local:scopeP=5) "during=$scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_parameter_existing_private_foreach_local", """
$private:scopeP='old'; 1 | ForEach-Object {param([int]$local:scopeP=5) "during=$scopeP"; & {"inner=$scopeP"}}; & {"child=$scopeP"}; "after=$scopeP"
""", [new("Output", "during=5"), new("Output", "inner=5"), new("Output", "child=5"), new("Output", "after=5")], false),
            ("scope_default_global_parameter_local_default", """
$x=1; function F {param($global:y=$local:x)}; F; "$x/$y"
""", [new("Output", "1/")], false),
            ("scope_default_function_local_default", """
$x='root'; function F {param($v=$local:x) "v=$v;local=$local:x;inherited=$x"}; F
""", [new("Output", "v=;local=;inherited=root")], false),
            ("scope_default_function_unqualified_default", """
$x='root'; function F {param($v=$x) "v=$v;local=$local:x;inherited=$x"}; F
""", [new("Output", "v=root;local=;inherited=root")], false),
            ("scope_default_function_private_default", """
$x='root'; function F {param($v=$private:x) "v=$v;local=$local:x;inherited=$x"}; F
""", [new("Output", "v=;local=;inherited=root")], false),
            ("scope_default_function_scope_zero_default", """
$x='root'; function F {param($v=(Get-Variable x -Scope 0 -ValueOnly -ErrorAction SilentlyContinue)) "v=$v;inherited=$x"}; F
""", [new("Output", "v=;inherited=root")], false),
            ("scope_default_function_scope_one_default", """
$x='root'; function F {param($v=(Get-Variable x -Scope 1 -ValueOnly)) "v=$v;inherited=$x"}; F
""", [new("Output", "v=root;inherited=root")], false),
            ("scope_default_nested_scope_one_default", """
$x='root'; function Outer { $x='parent'; function Inner {param($v=(Get-Variable x -Scope 1 -ValueOnly)) "v=$v;root=$global:x"}; Inner }; Outer
""", [new("Output", "v=parent;root=root")], false),
            ("scope_default_function_earlier_bound_local", """
function F {param($first,$second=$local:first) "$first/$second"}; F 7
""", [new("Output", "7/7")], false),
            ("scope_default_function_earlier_default_local", """
function F {param($first=7,$second=$local:first) "$first/$second"}; F
""", [new("Output", "7/7")], false),
            ("scope_default_function_later_bound_local", """
function F {param($first=$local:second,$second) "$first/$second"}; F -second 7
""", [new("Output", "7/7")], false),
            ("scope_default_function_later_default_local", """
$second=99; function F {param($first=$local:second,$second=7) "$first/$second"}; F
""", [new("Output", "/7")], false),
            ("scope_default_function_later_unqualified_default", """
$second=99; function F {param($first=$second,$second=7) "$first/$second"}; F
""", [new("Output", "99/7")], false),
            ("scope_default_function_default_local_side_effect", """
$mark='root'; function F {param($v=$($local:mark='default';7)) "$v/$mark/$local:mark/$global:mark"}; F; $mark
""", [new("Output", "7/default/default/root"), new("Output", "root")], false),
            ("scope_default_function_default_new_local_side_effect", """
function F {param($v=$($local:created='default';7)) "$v/$created/$local:created"}; F; "after=$created"
""", [new("Output", "7/default/default"), new("Output", "after=")], false),
            ("scope_default_function_default_unqualified_side_effect", """
$mark='root'; function F {param($v=$($mark='default';7)) "$v/$mark/$local:mark/$global:mark"}; F; $mark
""", [new("Output", "7/default/default/root"), new("Output", "root")], false),
            ("scope_default_function_default_global_side_effect", """
$mark='root'; function F {param($v=$($global:mark='default';7)) "$v/$mark/$local:mark/$global:mark"}; F; $mark
""", [new("Output", "7/default//default"), new("Output", "default")], false),
            ("scope_default_function_default_scope_one_write", """
$mark='root'; function F {param($v=$(Set-Variable mark 'parent' -Scope 1;7)) "$v/$mark/$local:mark"}; F; $mark
""", [new("Output", "7/parent/"), new("Output", "parent")], false),
            ("scope_default_function_default_bound_parameters", """
function F {param($first,$second="$($PSBoundParameters.Count)/$($PSBoundParameters['first'])") "$first/$second;body=$($PSBoundParameters.Count)"}; F 7
""", [new("Output", "7/0/;body=1")], false),
            ("scope_default_function_default_args", """
function F {param($first="count=$($args.Count)") "$first;body=$($args.Count)"}; F -other 2
""", [new("Output", "count=0;body=2")], false),
            ("scope_default_function_default_failure_local", """
$mark='root'; function F {param([int]$v=$($local:mark='default';'bad')) 'body'}; try {F} catch {'failed'}; "after=$mark"
""", [new("Output", "failed"), new("Output", "after=root")], false),
            ("scope_default_function_default_failure_global", """
$mark='root'; function F {param([int]$v=$($global:mark='default';'bad')) 'body'}; try {F} catch {'failed'}; "after=$mark"
""", [new("Output", "failed"), new("Output", "after=default")], false),
            ("scope_default_scriptblock_local_default", """
$x='root'; & {param($v=$local:x) "v=$v;local=$local:x;inherited=$x"}
""", [new("Output", "v=;local=;inherited=root")], false),
            ("scope_default_scriptblock_scope_one_default", """
$x='root'; & {param($v=(Get-Variable x -Scope 1 -ValueOnly)) "v=$v;inherited=$x"}
""", [new("Output", "v=root;inherited=root")], false),
            ("scope_default_scriptblock_default_side_effect", """
$mark='root'; & {param($v=$($local:mark='default';7)) "$v/$mark/$local:mark/$global:mark"}; $mark
""", [new("Output", "7/default/default/root"), new("Output", "root")], false),
            ("scope_default_dot_function_local_default", """
$x='root'; function F {param($v=$local:x) "v=$v;local=$local:x"}; . F; "after=$v"
""", [new("Output", "v=root;local=root"), new("Output", "after=root")], false),
            ("scope_default_dot_scriptblock_local_default", """
$x='root'; . {param($v=$local:x) "v=$v;local=$local:x"}; "after=$v"
""", [new("Output", "v=root;local=root"), new("Output", "after=root")], false),
            ("scope_default_dot_default_side_effect", """
$mark='root'; . {param($v=$($local:mark='default';7)) "$v/$mark"}; "after=$v/$mark"
""", [new("Output", "7/default"), new("Output", "after=7/default")], false),
            ("scope_default_foreach_local_default", """
$x='root'; 1 | ForEach-Object {param($v=$local:x) "v=$v;local=$local:x;inherited=$x"}
""", [new("Output", "v=root;local=root;inherited=root")], false),
            ("scope_default_collection_local_default", """
$x='root'; (1).ForEach({param($unused,$v=$local:x) "v=$v;local=$local:x;inherited=$x"})
""", [new("Output", "v=root;local=root;inherited=root")], false),
            ("scope_default_calculated_local_default", """
$x='root'; 1 | Select-Object @{Name='Value';Expression={param($v=$local:x) "v=$v;local=$local:x;inherited=$x"}} | ForEach-Object {$_.Value}
""", [new("Output", "v=;local=;inherited=root")], false),
            ("scope_default_foreach_default_side_effect", """
$mark='root'; 1 | ForEach-Object {param($v=$($local:mark='default';7)) "$v/$mark"}; "after=$v/$mark"
""", [new("Output", "7/default"), new("Output", "after=/default")], false),
            ("scope_default_function_earlier_default_mutation", """
function F {param($first=7,$second=$($first=8;9)) "$first/$second"}; F
""", [new("Output", "8/9")], false),
            ("scope_default_function_default_automatic_input", """
function F {param($v=@($input).Count) "v=$v;body=$(@($input).Count)"}; 1,2 | F
""", [new("Output", "v=0;body=2")], false),
            ("scope_default_function_default_output_capture", """
function F {param($v=$('first';'second')) "type=$($v.GetType().Name);count=$($v.Count);value=$($v -join ',')"}; F
""", [new("Output", "type=Object[];count=2;value=first,second")], false),
            ("scope_default_dot_later_local_default", """
$b=9;. {param($a=$local:b,$b=2) "$a/$b"};"root=$a/$b"
""", [new("Output", "9/2"), new("Output", "root=9/2")], false),
            ("scope_default_dot_later_unqualified_default", """
$b=9;. {param($a=$b,$b=2) "$a/$b"};"root=$a/$b"
""", [new("Output", "9/2"), new("Output", "root=9/2")], false),
            ("scope_default_nested_function_bound_parameters", """
function Outer {param($outer) function Inner {param($v="$($PSBoundParameters.Count)/$($PSBoundParameters['outer'])") "default=$v;body=$($PSBoundParameters.Count)"}; Inner; "outer=$($PSBoundParameters.Count)"}; Outer 7
""", [new("Output", "default=1/7;body=0"), new("Output", "outer=1")], false),
            ("scope_default_nested_dot_function_bound_parameters", """
function Outer {param($outer) function Inner {param($v="$($PSBoundParameters.Count)/$($PSBoundParameters['outer'])") "default=$v;body=$($PSBoundParameters.Count)"}; . Inner; "outer=$($PSBoundParameters.Count)"}; Outer 7
""", [new("Output", "default=1/7;body=0"), new("Output", "outer=1")], false),
            ("scope_default_nested_scriptblock_bound_parameters", """
function Outer {param($outer) & {param($v="$($PSBoundParameters.Count)/$($PSBoundParameters['outer'])") "default=$v;body=$($PSBoundParameters.Count)"}; "outer=$($PSBoundParameters.Count)"}; Outer 7
""", [new("Output", "default=1/7;body=0"), new("Output", "outer=1")], false),
            ("scope_default_nested_dot_scriptblock_bound_parameters", """
function Outer {param($outer) . {param($v="$($PSBoundParameters.Count)/$($PSBoundParameters['outer'])") "default=$v;body=$($PSBoundParameters.Count)"}; "outer=$($PSBoundParameters.Count)"}; Outer 7
""", [new("Output", "default=1/7;body=0"), new("Output", "outer=1")], false),
            ("scope_default_nested_function_args", """
function Outer {param($outer) function Inner {param($v=$args.Count) "default=$v;body=$($args.Count)"}; Inner -extra 4; "outer=$($args.Count)"}; Outer 7 8 9
""", [new("Output", "default=2;body=2"), new("Output", "outer=2")], false),
            ("scope_default_nested_dot_function_args", """
function Outer {param($outer) function Inner {param($v=$args.Count) "default=$v;body=$($args.Count)"}; . Inner -extra 4; "outer=$($args.Count)"}; Outer 7 8 9
""", [new("Output", "default=2;body=2"), new("Output", "outer=2")], false),
            ("scope_default_nested_scriptblock_args", """
function Outer {param($outer) & {param($v=$args.Count) "default=$v;body=$($args.Count)"} -extra 4; "outer=$($args.Count)"}; Outer 7 8 9
""", [new("Output", "default=2;body=2"), new("Output", "outer=2")], false),
            ("scope_default_nested_dot_scriptblock_args", """
function Outer {param($outer) . {param($v=$args.Count) "default=$v;body=$($args.Count)"} -extra 4; "outer=$($args.Count)"}; Outer 7 8 9
""", [new("Output", "default=2;body=2"), new("Output", "outer=2")], false),
            ("scope_default_nested_function_input", """
function Outer {function Inner {param($v=@($input).Count) "default=$v;body=$(@($input).Count)"}; 3,4,5 | Inner; "outer=$(@($input).Count)"}; 1,2 | Outer
""", [new("Output", "default=2;body=3"), new("Output", "outer=0")], false),
            ("scope_default_nested_dot_function_input", """
function Outer {function Inner {param($v=@($input).Count) "default=$v;body=$(@($input).Count)"}; 3,4,5 | . Inner; "outer=$(@($input).Count)"}; 1,2 | Outer
""", [new("Output", "default=2;body=3"), new("Output", "outer=0")], false),
            ("scope_default_nested_scriptblock_input", """
function Outer {3,4,5 | & {param($v=@($input).Count) "default=$v;body=$(@($input).Count)"}; "outer=$(@($input).Count)"}; 1,2 | Outer
""", [new("Output", "default=2;body=3"), new("Output", "outer=0")], false),
            ("scope_default_nested_dot_scriptblock_input", """
function Outer {3,4,5 | . {param($v=@($input).Count) "default=$v;body=$(@($input).Count)"}; "outer=$(@($input).Count)"}; 1,2 | Outer
""", [new("Output", "default=2;body=3"), new("Output", "outer=0")], false),
            ("scope_default_function_prior_typed_parameter_mutation", """
function F {param([int]$a=1,$b=$($a='bad';'done')) "$a/$b"};try{F}catch{'caught'}
""", [new("Output", "caught")], false),
            ("scope_default_dot_existing_typed_parameter_mutation", """
$a=9;try{. {param([int]$a=1,$b=$($a='bad';'done')) "$a/$b"}}catch{'caught'};"root=$a"
""", [new("Output", "bad/done"), new("Output", "root=bad")], false),
            ("scope_default_dot_new_typed_parameter_mutation", """
try{. {param([int]$a=1,$b=$($a='bad';'done')) "$a/$b"}}catch{'caught'};"root=$a"
""", [new("Output", "caught"), new("Output", "root=1")], false),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected, rejectsSyntax) in cases)
        {
            try
            {
                var runtime = new PowerShellWasmRuntime();
                PowerShellWasmResult? result = null;
                Exception? failure = null;
                try { result = await runtime.ExecuteAsync(script); }
                catch (Exception exception) { failure = exception; }
                if (rejectsSyntax)
                {
                    if (failure is not InvalidOperationException)
                        throw new InvalidOperationException($"Expected invalid variable syntax to be rejected; got {failure?.GetType().Name ?? "success"}.", failure);
                }
                else
                {
                    if (failure is not null) throw failure;
                    AssertRecords(name, result!, expected);
                }
            }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        Console.WriteLine($"Scoped variable scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
        await VerifySessionReuseAsync();
    }

    private static async ValueTask VerifySessionReuseAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        AssertRecords("scope session step 1", await runtime.ExecuteAsync("""
$global:count=1;[int]$script:typed=2;$private:secret='hidden';"count=$count;typed=$typed;secret=$secret"
"""), [new("Output", "count=1;typed=2;secret=hidden")]);
        AssertRecords("scope session step 2", await runtime.ExecuteAsync("""
& { $global:count+=1; $script:typed='3'; $local:temporary='child'; 1 | ForEach-Object { Set-Variable count 4 -Scope 1 }; "count=$global:count;typed=$script:typed;secret=$secret;temporary=$temporary" }; "root-temporary=$temporary"
"""), [new("Output", "count=4;typed=3;secret=;temporary=child"), new("Output", "root-temporary=")]);
        AssertRecords("scope session step 3", await runtime.ExecuteAsync("""
& { try {$script:typed='bad'} catch {'rejected'}; Set-Variable count 5 -Scope Global }; "count=$count;typed=$typed;secret=$secret"
"""), [new("Output", "rejected"), new("Output", "count=5;typed=3;secret=hidden")]);
        AssertRecords("scope session step 4", await runtime.ExecuteAsync("""
& { Get-Variable secret -Scope 1 -ValueOnly; Remove-Variable typed -Scope 1 }; $typed='word'; "$typed/$($typed -is [string])"; & { "secret=$global:secret;temporary=$temporary" }
"""), [new("Output", "hidden"), new("Output", "word/True"), new("Output", "secret=;temporary=")]);
        Console.WriteLine("Scoped variable session scenarios: 4/4 passed.");
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
