using PSWasm;
using PSWasm.Language;

internal static class NullConditionalAccessVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell 7.6.1 supplied the output, evaluation-order, and parse expectations.
        // A non-null conditional receiver is evaluated twice natively; a null receiver once.
        // Native conditional-access compilation checks and then reuses the receiver expression.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L6597-L6633
        VerifyAstCompatibility();
        var cases = new (string Name, string Script, string[] Expected)[]
        {
            ("null_index_shape",
                "${x}=$null; $r=@(${x}?[0]); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("null_index_skips_error",
                "${x}=$null; $r=${x}?[$(throw 'not-run')]; $null -eq $r",
                ["True"]),
            ("null_index_skips_assignment",
                "${x}=$null; $i=0; $r=${x}?[($i++)]; \"$($null -eq $r)|$i\"",
                ["True|0"]),
            ("array_scalar_index",
                "${x}=10,20,30; ${x}?[1]; ${x}?[-1]; $null -eq ${x}?[99]",
                ["20", "30", "True"]),
            ("array_slice",
                "${x}=10,20,30; $r=${x}?[2,0,2]; $r.GetType().Name; $r.Count; $r -join ','",
                ["Object[]", "3", "30,10,30"]),
            ("array_single_slice",
                "${x}=10,20,30; $r=${x}?[@(1)]; $r.GetType().Name; $r.Count; $r -join ','",
                ["Object[]", "1", "20"]),
            ("array_empty_slice",
                "${x}=10,20,30; $r=${x}?[@()]; $null -eq $r; $r.GetType().Name; $r.Count",
                ["False", "Object[]", "0"]),
            ("empty_array_receiver",
                "${x}=@(); $r=@(${x}?[0]); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("array_null_element",
                "${x}=1,$null,3; $r=@(${x}?[1]); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("array_null_index_rejected",
                "${x}=10,20; try {${x}?[$null]} catch {'rejected'}; 'after'",
                ["rejected", "after"]),
            ("string_index",
                "${x}='abc'; $r=${x}?[1]; $r; $r.GetType().Name; [int]$r",
                ["b", "Char", "98"]),
            ("string_slice",
                "${x}='abc'; $r=${x}?[2,0,-1]; $r.Count; $r -join ','; $r[0].GetType().Name",
                ["3", "c,a,c", "Char"]),
            ("empty_string_index",
                "${x}=''; $r=@(${x}?[0]); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("dictionary_index",
                "${x}=@{Name='abc';Value=7}; ${x}?['Name']; ${x}?['Value']; $null -eq ${x}?['Absent']",
                ["abc", "7", "True"]),
            ("dictionary_slice",
                "${x}=@{Name='abc';Value=7}; $r=${x}?['Value','Absent','Name']; $r.Count; $r -join '|'; $null -eq $r[1]",
                ["3", "7||abc", "True"]),
            ("scalar_index",
                "${x}=42; ${x}?[0]; ${x}?[-1]; $null -eq ${x}?[1]",
                ["42", "42", "True"]),
            ("typed_list_index",
                "${x}=[System.Collections.Generic.List[int]]@(10,20); ${x}?[1]; (${x}?[-1,0]) -join ','",
                ["20", "20,10"]),
            ("receiver_evaluated_twice",
                "$global:n=0; function Get-NC {$global:n++; 10,20}; (Get-NC)?[1]; $n",
                ["20", "2"]),
            ("null_receiver_evaluated_once",
                "$global:n=0; function Get-NC {$global:n++; $null}; $i=0; $r=@((Get-NC)?[($i++)]); $r.Count; $null -eq $r[0]; \"$n|$i\"",
                ["1", "True", "1|0"]),
            ("index_evaluated_once_after_receiver",
                "$global:trace=''; function Get-NC {$global:trace+='r'; 10,20}; function Get-Index {$global:trace+='i'; 1}; (Get-NC)?[(Get-Index)]; $trace",
                ["20", "rri"]),
            ("second_receiver_null",
                "$global:n=0; $global:trace=''; function Get-NC {$global:n++; if($n -eq 1){10,20}}; function Get-Index {$global:trace+='i'; 0}; try {(Get-NC)?[(Get-Index)]} catch {'failed'}; $n; $trace",
                ["failed", "2", "i"]),
            ("null_member_shape",
                "${x}=$null; $r=@(${x}?.Name); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("member_nonnull",
                "${x}=@{Name='abc'}; ${x}?.Name; $null -eq ${x}?.Missing; 'after'",
                ["abc", "True", "after"]),
            ("null_computed_member_lazy",
                "${x}=$null; $i=0; $r=${x}?.($i++); \"$($null -eq $r)|$i\"",
                ["True|0"]),
            ("computed_member_evaluation_order",
                "$global:trace=''; function Get-NC {$global:trace+='r'; @{Name='a'}}; function Get-Name {$global:trace+='n'; 'Name'}; (Get-NC)?.(Get-Name); $trace",
                ["a", "rrn"]),
            ("nonnull_method_arguments",
                "${x}='abc'; $i=0; ${x}?.Substring(($i++),1); $i",
                ["a", "1"]),
            ("null_method_arguments_lazy",
                "${x}=$null; $i=0; $r=@(${x}?.Substring(($i++),1)); $r.Count; $null -eq $r[0]; $i",
                ["1", "True", "0"]),
            ("computed_method_evaluation_order",
                "$global:trace=''; function Get-NC {$global:trace+='r'; 'abc'}; function Get-Name {$global:trace+='n'; 'Substring'}; function Get-Arg {$global:trace+='a'; 1}; (Get-NC)?.(Get-Name)((Get-Arg)); $trace",
                ["bc", "rrna"]),
            ("computed_null_method_lazy",
                "$global:trace=''; function Get-NC {$global:trace+='r'; $null}; function Get-Name {$global:trace+='n'; 'Substring'}; function Get-Arg {$global:trace+='a'; 1}; $r=@((Get-NC)?.(Get-Name)((Get-Arg))); $r.Count; $null -eq $r[0]; $trace",
                ["1", "True", "r"]),
            ("nonnull_conditional_chain",
                "${x}=@{Rows=@(@{Name='abc'})}; ${x}?.Rows?[0]?.Name?[1]",
                ["b"]),
            ("null_conditional_chain",
                "${x}=$null; $r=${x}?.Rows?[0]?.Name?[1]; $null -eq $r",
                ["True"]),
            ("ordinary_index_after_null_conditional",
                "${x}=$null; try {$r=${x}?[0][0]; 'not-run'} catch {'rejected'}",
                ["rejected"]),
            ("ordinary_method_after_null_conditional",
                "${x}=$null; try {$r=${x}?.Length.ToString(); 'not-run'} catch {'rejected'}",
                ["rejected"]),
            ("ordinary_null_index_before_conditional",
                "${x}=$null; try {$r=${x}[0]?[0]; 'not-run'} catch {'rejected'}",
                ["rejected"]),
            ("normal_outer_member_assignment",
                "${x}=@(@{Name='a'}); ${x}?[0].Name='b'; $x[0].Name",
                ["b"]),
            ("normal_outer_index_assignment",
                "${x}=,@(1); ${x}?[0][0]=2; $x[0][0]",
                ["2"]),
            ("normal_outer_index_update",
                "${x}=@{Rows=@(1)}; ${x}?.Rows[0]++; $x.Rows[0]",
                ["2"]),
            ("normal_outer_member_compound",
                "${x}=@(@{Count=1}); ${x}?[0].Count+=2; $x[0].Count",
                ["3"]),
            ("command_member_literal",
                "${x}='abc'; Write-Output ${x}?.Length; Write-Output (${x}?.Length)",
                ["abc?.Length", "3"]),
            ("command_index_literal",
                "${x}='abc'; Write-Output ${x}?[0]; Write-Output (${x}?[0])",
                ["abc?[0]", "a"]),
            ("command_null_literals",
                "${x}=$null; Write-Output ${x}?.Length; Write-Output ${x}?[0]",
                ["?.Length", "?[0]"]),
            ("command_slice_literal",
                "${x}='abc'; Write-Output ${x}?[0,1]",
                ["abc?[0", "1]"]),
            ("command_literal_index_expression_fragments",
                "${x}='abc'; $i=0; Write-Output ${x}?[($i++)]; $i",
                ["abc?[", "0", "]", "1"]),
            ("command_after_ordinary_member",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name?[0]; Write-Output ${x}.Name?.Length",
                ["a", "3"]),
            ("command_after_ordinary_index",
                "${x}=@('abc'); Write-Output ${x}[0]?[1]; Write-Output ${x}[0]?.Length",
                ["b", "3"]),
            ("command_after_parenthesized_target",
                "${x}='abc'; Write-Output (${x})?[0]; Write-Output (${x})?.Length",
                ["a", "3"]),
            ("unbraced_question_variable_index",
                "$x='abc'; $x?='def'; $x?[0]; ${x}?[0]",
                ["d", "a"]),
            ("unbraced_question_variable_member",
                "$x='abc'; $x?='de'; $x?.Length; ${x}?.Length",
                ["2", "3"]),
            ("index_newlines",
                "${x}=10,20; (${x}?[\n0,\n1\n]) -join ','",
                ["10,20"]),
            ("member_newline",
                "${x}=@{Name='abc'}; ${x}?.\nName",
                ["abc"]),
            ("quoted_primary_index",
                "'abc'?[0]",
                ["a"]),
            ("command_quoted_primary_index",
                "Write-Output 'abc'?[0]",
                ["a"]),
            ("command_ordinary_member_newline",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name?.\nLength",
                ["3"]),
            ("command_grouped_newline",
                "${x}='abc'; Write-Output (${x})?.\nLength",
                ["3"]),
            ("command_direct_literal_newline",
                "function Length {'second'}; ${x}='abc'; Write-Output ${x}?.\nLength",
                ["abc?.", "second"]),
            ("comment_adjacent_index",
                "${x}='abc'; ${x}<#c#>?[0]",
                ["a"]),
            ("comment_adjacent_member",
                "${x}=@{Name='abc'}; ${x}<#c#>?.Name",
                ["abc"]),
            ("comment_multiline_index",
                "${x}='abc'; ${x}<# first\nsecond #>?[0]",
                ["a"]),
            ("comment_multiline_member",
                "${x}=@{Name='abc'}; ${x}<# first\nsecond #>?.Name",
                ["abc"]),
            ("comment_command_grouped_index",
                "${x}='abc'; Write-Output (${x})<#c#>?[0]",
                ["a"]),
            ("comment_command_member_member",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name<#c#>?.Length",
                ["3"]),
            ("comment_command_member_index",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name<#c#>?[0]",
                ["a"]),
            ("comment_command_quoted_index",
                "Write-Output 'abc'<#c#>?[0]",
                ["a"]),
            ("comment_command_grouped_space",
                "${x}='abc'; Write-Output (${x}) <#c#>?[0]",
                ["abc", "?[0]"]),
            ("comment_command_member_newline",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name<#c#>?.\nLength",
                ["3"]),
            ("comment_command_grouped_newline",
                "${x}='abc'; Write-Output (${x})<#c#>?.\nLength",
                ["3"]),
            ("comment_repeated_adjacent",
                "${x}='abc'; ${x}<#a#><#b#>?[0]",
                ["a"]),
            ("lexical_quoted_question_member",
                "${x}=@{'Rows?'=@('yes')}; ${x}?.'Rows?'[0]",
                ["yes"]),
            ("lexical_generic_question_arguments",
                "Write-Output abc? xyz?.Name path?[0]",
                ["abc?", "xyz?.Name", "path?[0]"]),
            ("lexical_generic_comment_arguments",
                "Write-Output prefix<#c#>suffix prefix<#c#>?[0] prefix<#c#>?.Name",
                ["prefix<#c#>suffix", "prefix<#c#>?[0]", "prefix<#c#>?.Name"]),
            ("lexical_generic_escaped_question",
                "Write-Output abc`? xyz`?.Name path`?[0]",
                ["abc?", "xyz?.Name", "path?[0]"]),
            ("lexical_initial_variable_comment_arguments",
                "${x}='abc'; Write-Output ${x}<#c#>?[0]; Write-Output ${x}<#c#>?.Length",
                ["abc<#c#>?[0]", "abc<#c#>?.Length"]),
            ("lexical_direct_literal_then_generic_comment",
                "${x}='abc'; Write-Output ${x}?. prefix<#c#>",
                ["abc?.", "prefix<#c#>"]),
            ("lexical_direct_literal_then_question_command",
                "function Name? {'question-command'}; ${x}='abc'; Write-Output ${x}?.\nName?",
                ["abc?.", "question-command"]),
            ("lexical_generic_dotted_comment_argument",
                "Write-Output .Name<#c#>",
                [".Name<#c#>"]),
        };
        var diagnostics = new (string Name, string Script)[]
        {
            ("lexical_escaped_question_member_invalid", "${x}=@{'Name?'='question';Name='normal'}; ${x}.Name`?"),
            ("lexical_escaped_question_conditional_invalid", "${x}=@{'Rows?'=@('yes')}; ${x}?.Rows`?[0]"),
            ("comment_index_space_before_invalid", "${x}='abc'; ${x} <#c#>?[0]"),
            ("comment_index_space_after_invalid", "${x}='abc'; ${x}<#c#> ?[0]"),
            ("comment_member_space_before_invalid", "${x}=@{Name='abc'}; ${x} <#c#>?.Name"),
            ("comment_member_space_after_invalid", "${x}=@{Name='abc'}; ${x}<#c#> ?.Name"),
            ("comment_inside_index_operator_invalid", "${x}='abc'; ${x}?<#c#>[0]"),
            ("comment_inside_member_operator_invalid", "${x}=@{Name='abc'}; ${x}?<#c#>.Name"),
            ("comment_space_between_invalid", "${x}='abc'; ${x}<#a#> <#b#>?[0]"),
            ("index_assignment_invalid", "${x}=1,2; ${x}?[0]=3; 'not-run'"),
            ("index_compound_invalid", "${x}=1,2; ${x}?[0]+=3; 'not-run'"),
            ("index_coalescing_assignment_invalid", "${x}=1,2; ${x}?[0]??=3; 'not-run'"),
            ("index_postfix_invalid", "${x}=1,2; ${x}?[0]++; 'not-run'"),
            ("index_prefix_invalid", "${x}=1,2; ++${x}?[0]; 'not-run'"),
            ("member_assignment_invalid", "${x}=@{Name='a'}; ${x}?.Name='b'; 'not-run'"),
            ("computed_member_assignment_invalid", "${x}=@{Name='a'}; ${x}?.('Name')='b'; 'not-run'"),
            ("member_postfix_invalid", "${x}=@{Count=1}; ${x}?.Count++; 'not-run'"),
            ("conditional_chain_assignment_invalid", "${x}=@(@{Name='a'}); ${x}?[0]?.Name='b'; 'not-run'"),
            ("index_space_before_invalid", "${x}='abc'; ${x} ?[0]"),
            ("index_space_inside_invalid", "${x}='abc'; ${x}? [0]"),
            ("index_empty_invalid", "${x}='abc'; ${x}?[]"),
            ("index_trailing_comma_invalid", "${x}='abc'; ${x}?[0,]"),
            ("member_space_before_invalid", "${x}=@{Name='a'}; ${x} ?.Name"),
            ("member_space_inside_invalid", "${x}=@{Name='a'}; ${x}? .Name"),
            ("command_empty_method_invalid", "${x}='abc'; Write-Output ${x}?.ToUpper()"),
        };
        var failures = new List<string>();
        foreach (var (name, script, expected) in cases)
        {
            try
            {
                var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
                var records = expected.Select(static text => new PowerShellWasmOutputRecord("Output", text));
                if (!result.Records.SequenceEqual(records))
                    failures.Add($"{name}: expected {string.Join(" | ", expected)}; actual {result.Text}");
            }
            catch (Exception exception) { failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}"); }
        }
        var outputFailures = failures.Count;
        Console.WriteLine($"Null-conditional output scenarios: {cases.Length - outputFailures}/{cases.Length} passed.");
        foreach (var (name, script) in diagnostics)
        {
            try
            {
                var parserRejected = false;
                try { _ = new PowerShellWasmParser().Parse(script); }
                catch (InvalidOperationException) { parserRejected = true; }
                var runtimeRejected = false;
                try { await new PowerShellWasmRuntime().ExecuteAsync(script); }
                catch (InvalidOperationException) { runtimeRejected = true; }
                if (!parserRejected || !runtimeRejected)
                    failures.Add($"{name}: expected parser and runtime rejection; parser={parserRejected}; runtime={runtimeRejected}.");
            }
            catch (Exception exception) { failures.Add($"{name}: unexpected {exception.GetType().Name}: {exception.Message}"); }
        }
        Console.WriteLine($"Null-conditional diagnostics: {diagnostics.Length - (failures.Count - outputFailures)}/{diagnostics.Length} passed.");
        await VerifyParseBeforeExecutionAsync();
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static void VerifyAstCompatibility()
    {
        var target = new VariableExpressionAst("value", false);
        var index = new NumberExpressionAst(0);
        var legacy = new IndexExpressionAst(target, index);
        var (originalTarget, originalIndex) = legacy;
        var conditional = legacy with { NullConditional = true };
        var edited = conditional with { Index = new NumberExpressionAst(1) };
        if (originalTarget != target || originalIndex != index || legacy.NullConditional ||
            !conditional.NullConditional || !edited.NullConditional || edited.Index != new NumberExpressionAst(1))
            throw new InvalidOperationException("Existing index AST construction/deconstruction and with-expression edits must remain compatible.");
        Console.WriteLine("Null-conditional index AST compatibility: passed.");
    }

    private static async ValueTask VerifyParseBeforeExecutionAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        await runtime.ExecuteAsync("$NcSentinel = 'unchanged'");
        string[] invalidScripts =
        [
            "$NcSentinel='changed'; ${x}=1,2; ${x}?[0]=3",
            "$NcSentinel='changed'; ${x}=@{Name='a'}; ${x}?.Name='b'",
        ];
        foreach (var script in invalidScripts)
        {
            var rejected = false;
            try { await runtime.ExecuteAsync(script); }
            catch (InvalidOperationException) { rejected = true; }
            var result = await runtime.ExecuteAsync("$NcSentinel");
            if (!rejected || !result.Records.SequenceEqual([new PowerShellWasmOutputRecord("Output", "unchanged")]))
                throw new InvalidOperationException("Invalid null-conditional assignment must reject the whole script before execution.");
        }
        Console.WriteLine("Null-conditional parse-before-execution: 2/2 passed.");
    }
}
