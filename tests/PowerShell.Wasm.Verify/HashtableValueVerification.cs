using PSWasm;

internal static class HashtableValueVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected output and syntax rejection were independently verified with native PowerShell 7.6.1.
        // These exercise hash-entry statement grammar and complete index expressions, including unary comma shape.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected, bool RejectsSyntax)[]
        {
            ("array_rhs", """
$h=@{Items=1,2,3;Next=4}; "$($h.Items -join '/')/$($h.Next)/$($h.Items.Count)"
""", [new("Output", "1/2/3/4/3")], false),
            ("nested_array_rhs", """
$h=@{Items=,(1,2);Other=3,,4}; "$($h.Items.Count)/$($h.Items[0] -join '/')"; "$($h.Other.Count)/$($h.Other[1].Count)"
""", [new("Output", "1/1/2"), new("Output", "2/1")], false),
            ("empty_array_rhs", """
$h=@{Items=@();Other=$null}; "$($h.Items.Count)/$($h.Contains('Items'))/$($h.ContainsValue($null))"
""", [new("Output", "0/True/True")], false),
            ("expression_rhs", """
$h=@{A=1+2*3;B=1..3;C=$true ? 'yes':'no'}; "$($h.A)/$($h.B -join '/')/$($h.C)"
""", [new("Output", "7/1/2/3/yes")], false),
            ("command_multi_rhs", """
$h=@{Items=Write-Output 1 2;Next=3}; "$($h.Items -join '/')/$($h.Next)"
""", [new("Output", "1/2/3")], false),
            ("command_scalar_rhs", """
$h=@{Items=Write-Output 'one'}; "$($h.Items)/$($h.Items -is [string])"
""", [new("Output", "one/True")], false),
            ("bare_function_rhs", """
function Values {1;2};$h=@{Items=Values};$h.Items -join '/'
""", [new("Output", "1/2")], false),
            ("command_pipeline_rhs", """
$h=@{Items=1..4 | Where-Object {$_ -gt 2} | ForEach-Object {$_*2};Next=9}; "$($h.Items -join '/')/$($h.Next)"
""", [new("Output", "6/8/9")], false),
            ("call_operator_rhs", """
$h=@{Items=& {1;2};Next=3};$h.Items -join '/'
""", [new("Output", "1/2")], false),
            ("splat_command_rhs", """
$o=@{InputObject=1,2};$h=@{Items=Write-Output @o};$h.Items -join '/'
""", [new("Output", "1/2")], false),
            ("pipeline_chain_rhs", """
$h=@{Items=Write-Output 'a' && Write-Output 'b';Next=2};$h.Items -join '/'
""", [new("Output", "a/b")], false),
            ("command_no_output_rhs", """
function Empty {}; $h=@{A=Empty}; "$($null -eq $h.A)/$($h.ContainsValue($null))/$($h.Contains('A'))"
""", [new("Output", "True/False/True")], false),
            ("expression_no_output_rhs", """
$h=@{A=$(& {});B=$null}; "$($h.Contains('A'))/$($h.ContainsValue($null))"; $h.Remove('B');$h.ContainsValue($null)
""", [new("Output", "True/True"), new("Output", "False")], false),
            ("empty_if_rhs", """
$h=@{A=if($false){1}}; "$($null -eq $h.A)/$($h.ContainsValue($null))"
""", [new("Output", "True/False")], false),
            ("explicit_null_output_rhs", """
$h=@{A=Write-Output $null}; "$($null -eq $h.A)/$($h.ContainsValue($null))"
""", [new("Output", "True/True")], false),
            ("null_and_value_rhs", """
$h=@{A=Write-Output $null 1}; "$($h.A.Count)/$($null -eq $h.A[0])/$($h.A[1])"
""", [new("Output", "2/True/1")], false),
            ("if_rhs", """
$h=@{A=if($false){1}elseif($true){2;3}else{4};B=5}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "2/3/5")], false),
            ("if_nested_rhs", """
$h=@{A=if($true){if($true){'nested'};@{B=2}}}; "$($h.A[0])/$($h.A[1].B)"
""", [new("Output", "nested/2")], false),
            ("switch_rhs", """
$h=@{A=switch(1,2,3){1{'one'}2{'two'}default{'other'}}};$h.A -join '/'
""", [new("Output", "one/two/other")], false),
            ("foreach_rhs", """
$h=@{A=foreach($i in 1..3){$i*2};B=$i}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "2/4/6/3")], false),
            ("for_rhs", """
$h=@{A=for($i=0;$i -lt 3;$i++){$i};B=$i}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "0/1/2/3")], false),
            ("while_rhs", """
$i=0;$h=@{A=while($i -lt 3){$i++;$i};B=$i}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/3/3")], false),
            ("do_while_rhs", """
$i=0;$h=@{A=do{$i++;$i}while($i -lt 2);B=$i}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/2")], false),
            ("do_until_rhs", """
$i=0;$h=@{A=do{$i++;$i}until($i -eq 2);B=$i}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/2")], false),
            ("try_catch_finally_rhs", """
$h=@{A=try{'start';throw 'failure'}catch{'caught'}finally{'cleanup'};B=2}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "start/caught/cleanup/2")], false),
            ("try_finally_rhs", """
$h=@{A=try{'one'}finally{'two'}};$h.A -join '/'
""", [new("Output", "one/two")], false),
            ("assignment_rhs", """
$x=0;$h=@{A=$x=3;B=$x}; "$($h.A -eq $null)/$($h.ContainsValue($null))/$($h.B)"
""", [new("Output", "False/False/3")], false),
            ("compound_assignment_rhs", """
$x=1;$h=@{A=$x+=2;B=$x}; "$($h.A -eq $null)/$($h.ContainsValue($null))/$($h.B)"
""", [new("Output", "False/False/3")], false),
            ("grouped_assignment_rhs", """
$h=@{A=($x=1,2);B=$x}; "$($h.A -join '/')/$($h.B -join '/')"
""", [new("Output", "1/2/1/2")], false),
            ("script_block_rhs", """
$h=@{A={1;2}};$h.A -is [scriptblock]; & $h.A
""", [new("Output", "True"), new("Output", "1"), new("Output", "2")], false),
            ("nested_hash_rhs", """
$h=@{A=@{B=1,2;C=if($true){3}};D=4}; "$($h.A.B -join '/')/$($h.A.C)/$($h.D)"
""", [new("Output", "1/2/3/4")], false),
            ("ordered_rhs", """
$h=[ordered]@{A=1,2;B=if($true){3;4};C=5}; "$($h.Keys -join '/')/$($h.A -join '/')/$($h.B -join '/')"
""", [new("Output", "A/B/C/1/2/3/4")], false),
            ("custom_object_rhs", """
$h=[pscustomobject]@{A=1,2;B=Write-Output 'text'}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/text")], false),
            ("newline_entries", """
$h=@{
A=
1,2
B=3
}
"$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/3")], false),
            ("newline_array", """
$h=@{A=1,
2,
3
B=4}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/3/4")], false),
            ("newline_pipe", """
$h=@{A=1,2 |
ForEach-Object {$_*2}
B=5}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "2/4/5")], false),
            ("newline_if_else", """
$h=@{A=if($false){1}
else{2}
B=3}; "$($h.A)/$($h.B)"
""", [new("Output", "2/3")], false),
            ("newline_try_catch_finally", """
$h=@{A=try{throw 'x'}
catch{'caught'}
finally{'done'}
B=3}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "caught/done/3")], false),
            ("newline_do_while", """
$i=0;$h=@{A=do{$i++;$i}
while($i -lt 2)
B=3}; "$($h.A -join '/')/$($h.B)"
""", [new("Output", "1/2/3")], false),
            ("nested_semicolons", """
$h=@{A=$(1;2);B=@(3;4);C=if($true){5;6}}; "$($h.A -join '/')/$($h.B -join '/')/$($h.C -join '/')"
""", [new("Output", "1/2/3/4/5/6")], false),
            ("trailing_separators", """
$h=@{A=1;;B=2;
}; "$($h.A)/$($h.B)"
""", [new("Output", "1/2")], false),
            ("side_effect_order", """
$order='';$h=@{$($order+='k1';'a')=if($true){$order+='v1';1};$($order+='k2';'b')=if($true){$order+='v2';2}};$order
""", [new("Output", "k1v1k2v2")], false),
            ("dynamic_duplicate_side_effects", """
$key='a';$order='';try{$h=@{$key=if($true){$order+='one';1};$key=if($true){$order+='two';2}}}catch{'duplicate'};$order
""", [new("Output", "duplicate"), new("Output", "onetwo")], false),
            ("null_key_value_side_effect", """
$key=$null;$order='';try{$h=@{$key=if($true){$order+='value';1}}}catch{'null-key'};$order
""", [new("Output", "null-key"), new("Output", "value")], false),
            ("return_rhs", """
function F {$h=@{A=if($true){'before';return 'end'}};'after'};F
""", [new("Output", "before"), new("Output", "end")], false),
            ("break_rhs", """
foreach($i in 1..3){$h=@{A=if($i -eq 2){break}else{$i}};$h.A};'done'
""", [new("Output", "1"), new("Output", "done")], false),
            ("failed_rhs_preserves_previous_assignment", """
$h='old';try{$h=@{A=try{'partial';throw 'failure'}finally{'cleanup'}}}catch{'caught'};$h
""", [new("Output", "caught"), new("Output", "old")], false),
            ("comma_entry_invalid", """
@{A=1,B=2}
""", [], true),
            ("missing_value_invalid", """
@{A=;B=2}
""", [], true),
            ("missing_final_value_invalid", """
@{A=}
""", [], true),
            ("missing_equals_invalid", """
@{A;B=2}
""", [], true),
            ("missing_separator_invalid", """
@{A=1 B=2}
""", [], true),
            ("assignment_trailing_invalid", """
@{A=$x=1 2}
""", [], true),
            ("leading_semicolon_invalid", """
@{;A=1}
""", [], true),
            ("leading_comma_invalid", """
@{,A=1}
""", [], true),
            ("trailing_comma_invalid", """
@{A=1,}
""", [], true),
            ("missing_close_invalid", """
@{A=1
""", [], true),
            ("key_newline_equals_invalid", """
@{A
=1}
""", [], true),
            ("constant_duplicate_invalid", """
$x=0;@{A=if($true){$x++;1};a=2}
""", [], true),
            ("index_empty_invalid", """
$a=1,2;$a[]
""", [], true),
            ("index_trailing_comma_invalid", """
$a=1,2;$a[0,]
""", [], true),
            ("index_assignment_invalid", """
$a=1,2;$a[$x=0]
""", [], true),
            ("index_statement_separator_invalid", """
$a=1,2;$a[0;1]
""", [], true),
            ("index_missing_close_invalid", """
$a=1,2;$a[0
""", [], true),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected, rejectsSyntax) in cases)
        {
            try
            {
                var runtime = new PowerShellWasmRuntime();
                PowerShellWasmResult? result = null;
                Exception? failure = null;
                try
                {
                    if (rejectsSyntax) runtime.Parse(script);
                    else result = await runtime.ExecuteAsync(script);
                }
                catch (Exception exception) { failure = exception; }
                if (rejectsSyntax)
                {
                    if (failure is not InvalidOperationException)
                        throw new InvalidOperationException($"Expected invalid syntax to be rejected; got {failure?.GetType().Name ?? "success"}.", failure);
                }
                else
                {
                    if (failure is not null) throw failure;
                    if (!result!.Records.SequenceEqual(expected))
                    {
                        var expectedText = string.Join(Environment.NewLine, expected.Select(static record => $"{record.Stream}: {record.Text}"));
                        var actualText = string.Join(Environment.NewLine, result.Records.Select(static record => $"{record.Stream}: {record.Text}"));
                        throw new InvalidOperationException($"Expected records:{Environment.NewLine}{expectedText}" +
                            $"{Environment.NewLine}Actual records:{Environment.NewLine}{actualText}");
                    }
                }
            }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        Console.WriteLine($"Hashtable value and delimiter scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
    }
}
