using PSWasm;
using PSWasm.Language;

internal static class MemberCommentVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected output and parse rejection were verified against native PowerShell 7.6.1.
        // Comments are recognized in member/simple-name context, while generic command words retain literal delimiters.
        VerifyPublicTokenCompatibility();
        VerifyCommentedTypeParseCompatibility();
        var cases = new (string Name, string Script, string[] Expected)[]
        {
            ("conditional_index_spaced",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# comment #>?[0]",
                ["10"]),
            ("ordinary_index_spaced",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# comment #>[1]",
                ["20"]),
            ("conditional_index_tab",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<#\tcomment\t#>?[0]",
                ["10"]),
            ("ordinary_index_tab",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<#\tcomment\t#>[1]",
                ["20"]),
            ("conditional_index_lf",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# comment\nsecond line #>?[0]",
                ["10"]),
            ("ordinary_index_lf",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# comment\nsecond line #>[1]",
                ["20"]),
            ("conditional_index_crlf",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# comment\r\nsecond line #>?[0]",
                ["10"]),
            ("ordinary_index_crlf",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# comment\r\nsecond line #>[1]",
                ["20"]),
            ("conditional_index_punctuation",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# $bad?.Member; ' \" ` } ) ] | + ? : #>?[0]",
                ["10"]),
            ("ordinary_index_punctuation",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# $bad?.Member; ' \" ` } ) ] | + ? : #>[1]",
                ["20"]),
            ("conditional_index_empty",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<##>?[0]",
                ["10"]),
            ("ordinary_index_empty",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<##>[1]",
                ["20"]),
            ("conditional_index_repeated",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# first #><# second #>?[0]",
                ["10"]),
            ("ordinary_index_repeated",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# first #><# second #>[1]",
                ["20"]),
            ("ordinary_member_chain",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# gap #>.Count",
                ["2"]),
            ("conditional_member_chain",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# gap #>?.Length",
                ["3"]),
            ("static_member",
                "[int]::MaxValue<# gap #>; 'after'",
                ["2147483647", "after"]),
            ("comment_after_quoted_member",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.'Rows'<# gap #>[1]",
                ["20"]),
            ("comment_after_computed_member",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.('Rows')<# gap #>[1]",
                ["20"]),
            ("comment_before_member_name",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.<# gap #>Rows[1]",
                ["20"]),
            ("conditional_before_member_name",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.<# gap #>Rows[1]",
                ["20"]),
            ("ordinary_operator",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #>.Length + 2",
                ["5"]),
            ("conditional_operator",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# gap #>.Length + 2",
                ["5"]),
            ("ordinary_end_statement",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #>; 'after'",
                ["abc", "after"]),
            ("conditional_end_statement",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# gap #>; 'after'",
                ["abc", "after"]),
            ("ordinary_end_line",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #>\n'after'",
                ["abc", "after"]),
            ("conditional_end_line",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# gap #>\n'after'",
                ["abc", "after"]),
            ("ordinary_pipeline",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# gap #> | ForEach-Object {$_ + 1}",
                ["11", "21"]),
            ("conditional_pipeline",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# gap #> | ForEach-Object {$_ + 1}",
                ["11", "21"]),
            ("ordinary_array_comma",
                "${x}=@{Rows=@(10,20);Name='abc'}; (${x}.Name<# gap #>, 'after') -join '|'",
                ["abc|after"]),
            ("conditional_array_comma",
                "${x}=@{Rows=@(10,20);Name='abc'}; (${x}?.Name<# gap #>, 'after') -join '|'",
                ["abc|after"]),
            ("ordinary_assignment",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #>='changed'; ${x}.Name",
                ["changed"]),
            ("method_arg_member_comment",
                "${x}=@{Start=1}; 'abc'.Substring(${x}.Start<# gap #>)",
                ["bc"]),
            ("scriptblock_member_comment",
                "${x}=@{Rows=@(10,20);Name='abc'}; & {${x}.Rows<# gap #>[0]}",
                ["10"]),
            ("hashtable_value_comment",
                "${x}=@{Rows=@(10,20);Name='abc'}; $result=@{A=${x}.Rows<# gap #>[1];B='next'}; $result.A; $result.B",
                ["20", "next"]),
            ("catch_scope_member_comment",
                "try {throw 'boom'} catch {$_.Exception<# gap #>.Message}",
                ["boom"]),
            ("finally_scope_member_comment",
                "try {'first'} finally {'abc'.Length<# gap #>}",
                ["first", "3"]),
            ("member_comment_newline_inside_then_statement",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# internal\nnewline #>; 'after'",
                ["abc", "after"]),
            ("member_comment_escaped_quotes_inside",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# 'unterminated \" $(``bad` #>.Length",
                ["3"]),
            ("member_comment_false_terminator",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# # > x < # y #>.Length",
                ["3"]),
            ("null_receiver_skips_index",
                "${x}=$null; $i=0; $r=@(${x}?.Rows<# gap #>?[($i++)]); $r.Count; $null -eq $r[0]; $i",
                ["1", "True", "0"]),
            ("command_parenthesized_member",
                "${x}=@{Rows=@(10,20);Name='abc'}; Write-Output (${x}?.Rows<# gap #>?[0])",
                ["10"]),
            ("command_grouped_target",
                "${x}=@{Rows=@(10,20);Name='abc'}; Write-Output (${x})?.Rows<# gap #>?[1]",
                ["20"]),
            ("command_ordinary_member",
                "${x}=@{Name='abc'}; Write-Output ${x}.Name<# gap #>; 'after'",
                ["abc", "after"]),
            ("command_ordinary_member_continue",
                "${x}=@{Rows=@(10,20);Name='abc'}; Write-Output ${x}.Rows<# gap #>[1]; 'after'",
                ["20", "after"]),
            ("command_conditional_initial",
                "${x}='abc'; Write-Output ${x}?.Length<# gap #>; 'after'",
                ["abc?.Length<#", "gap"]),
            ("command_generic_spaced",
                "Write-Output prefix<# gap #>suffix; 'after'",
                ["prefix<#", "gap"]),
            ("command_generic_dotted_spaced",
                "Write-Output .Name<# gap #>suffix; 'after'",
                [".Name<#", "gap"]),
            ("command_generic_compact",
                "Write-Output prefix<#c#>suffix .Name<#c#> '${x}?.Name'",
                ["prefix<#c#>suffix", ".Name<#c#>", "${x}?.Name"]),
            ("command_literal_next_line",
                "function Name? {'question-command'}; ${x}='abc'; Write-Output ${x}?.\nName?",
                ["abc?.", "question-command"]),
            ("command_literal_next_argument",
                "${x}='abc'; Write-Output ${x}?. prefix<#c#>",
                ["abc?.", "prefix<#c#>"]),
            ("multiple_member_expressions",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# first #>; ${x}.Rows<# second #>[1]",
                ["abc", "20"]),
            ("comment_first_close_wins",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# outer <# inner #>[1]",
                ["20"]),
            ("conditional_comment_first_close_wins",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# outer <# inner #>?[1]",
                ["20"]),
            ("comment_contains_fake_statements",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# '; throw 'never'; } ] #>.Length; 'after'",
                ["3", "after"]),
            ("quoted_member_comment_text",
                "${x}=@{'Name<# comment #>'='literal'}; ${x}.'Name<# comment #>'",
                ["literal"]),
            ("quoted_comment_text_after_expression",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #>; 'literal <# text #>'",
                ["abc", "literal <# text #>"]),
            ("range_after_member_comment",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name.Length<# gap #>..5",
                ["3", "4", "5"]),
            ("index_assignment_after_member_comment",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# gap #>[0]=33; ${x}.Rows[0]",
                ["33"]),
            ("member_comment_then_newline_command",
                "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# internal\nnewline #>[1]\nWrite-Output 'after'",
                ["20", "after"]),
            ("conditional_null_comment_punctuation",
                "${x}=$null; $r=@(${x}?.Rows<# ' \" ` } ] | ? #>?[0]); $r.Count; $null -eq $r[0]",
                ["1", "True"]),
            ("static_member_index_after_comment",
                "[int]::MaxValue<# gap #>[0]",
                ["2147483647"]),
            ("method_return_then_comment",
                "'abc'.Substring(1)<# gap #>.Length",
                ["2"]),
            ("static_method_return_then_comment",
                "[Math]::Abs(-3)<# gap #>.ToString()",
                ["3"]),
            ("inside_catch_rethrow",
                "try {try {throw 'boom'} catch {$_.Exception<# gap #>.Message; throw}} catch {$_.Exception.Message}",
                ["boom", "boom"]),
            ("function_name_compact_comment_literal",
                "function Function<#c#> {'value'}; & 'Function<#c#>'",
                ["value"]),
            ("parameter_name_compact_comment_literal",
                "function Test-Literal {param(${Name<#c#>}) ${Name<#c#>}}; Test-Literal -Name<#c#> 'value'",
                ["value"]),
            ("hashtable_unquoted_comment_key",
                "$h=@{Name<# gap #>='value'}; $h.Name",
                ["value"]),
            ("hashtable_quoted_comment_key",
                "$h=@{'Name<# gap #>'='value'}; $h.'Name<# gap #>'",
                ["value"]),
            ("quoted_keys_before_comment",
                "$h=@{'.Name'<# gap #>=1;'Name?'<# gap #>=2;'Name-Value'<# gap #>=3}; $h['.Name']; $h['Name?']; $h['Name-Value']",
                ["1", "2", "3"]),
            ("type_cast_int",
                "[int<# gap #>]'12'",
                ["12"]),
            ("type_cast_multiline",
                "[int<# first\nsecond #>]'12'",
                ["12"]),
            ("type_literal_name",
                "([int<# gap #>]).Name",
                ["Int32"]),
            ("type_static_primitive",
                "[int<# gap #>]::MaxValue",
                ["2147483647"]),
            ("type_static_math",
                "[System.Math<# gap #>]::PI -gt 3",
                ["True"]),
            ("type_typed_variable",
                "[int<# gap #>]$v='12'; $v.GetType().Name; $v",
                ["Int32", "12"]),
            ("type_typed_parameter",
                "function Test-Type {param([int<# gap #>]$v) $v.GetType().Name; $v}; Test-Type '12'",
                ["Int32", "12"]),
            ("type_array_cast",
                "$v=[int<# gap #>[]]@(1,2); $v.GetType().Name; $v -join ','",
                ["Int32[]", "1,2"]),
            ("type_array_parameter",
                "function Test-Type {param([int<# gap #>[]]$v) $v.GetType().Name; $v -join ','}; Test-Type '1','2'",
                ["Int32[]", "1,2"]),
            ("type_generic_inner",
                "$v=[System.Collections.Generic.List[int<# gap #>]]@(1,2); $v.Count; $v[1]",
                ["2", "2"]),
            ("type_generic_outer",
                "$v=[System.Collections.Generic.List[int]<# gap #>]@(1,2); $v.Count; $v[1]",
                ["2", "2"]),
            ("type_generic_before_argument",
                "$v=[System.Collections.Generic.List<# gap #>[int]]@(1,2); $v.Count; $v[1]",
                ["2", "2"]),
            ("type_multiple_comments",
                "[int<# one #><# two #>]'12'",
                ["12"]),
            ("type_ordered_casts",
                "$v=[int<# gap #>][string]12; $v.GetType().Name; $v",
                ["Int32", "12"]),
            ("type_validate_set",
                "function Test-Type {param([ValidateSet<# gap #>('a','b')]$v) $v}; Test-Type 'a'",
                ["a"]),
            ("type_parameter_attribute",
                "function Test-Type {param([Parameter<# gap #>()][int]$v) $v.GetType().Name; $v}; Test-Type '12'",
                ["Int32", "12"]),
            ("type_attribute_quoted_comment",
                "function Test-Type {param([ValidateSet('<#literal#>')]$v) $v}; Test-Type '<#literal#>'",
                ["<#literal#>"]),
            ("type_bare_parameter",
                "param([int<# gap #>]$v=12) $v.GetType().Name; $v",
                ["Int32", "12"]),
            ("type_signature_parameter",
                "function Test-Type([int<# gap #>]$v) {$v.GetType().Name; $v}; Test-Type '12'",
                ["Int32", "12"]),
            ("type_catch_clause",
                "try {throw 'boom'} catch [System.Exception<# gap #>] {'caught'}",
                ["caught"]),
        };
        var diagnostics = new (string Name, string Script)[]
        {
            ("conditional_index_nested", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Rows<# outer <# inner #> outer #>?[0]"),
            ("ordinary_index_nested", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# outer <# inner #> outer #>[1]"),
            ("ordinary_method", "${x}='abcd'; ${x}.Substring<# gap #>(1,2)"),
            ("conditional_method", "${x}='abcd'; ${x}?.Substring<# gap #>(1,2)"),
            ("static_method", "[Math]::Abs<# gap #>(-4)"),
            ("null_receiver_skips_method_args", "${x}=$null; $i=0; $r=@(${x}?.Substring<# gap #>(($i++),1)); $r.Count; $null -eq $r[0]; $i"),
            ("spaces_outside_comment_before_index", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# gap #> [0]"),
            ("spaces_outside_comment_before_member", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# gap #> .Length"),
            ("unterminated_comment_ordinary", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# missing end"),
            ("unterminated_comment_conditional", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# missing end"),
            ("unterminated_nested_comment", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Name<# outer <# inner #> tail"),
            ("unterminated_comment_method", "${x}='abc'; ${x}.Substring<# missing"),
            ("unclosed_member_index_after_comment", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Rows<# gap #>["),
            ("conditional_assignment_after_comment", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}?.Name<# gap #>='changed'"),
            ("escaped_member_with_comment", "${x}=@{Rows=@(10,20);Name='abc'}; ${x}.Na`me<# gap #>"),
            ("invalid_comment_before_call_sentinel", "$MemberCommentSentinel='changed'; ${x}='abc'; ${x}.Substring<# gap #>(1)"),
            ("invalid_comment_ordinary_sentinel", "$MemberCommentSentinel='changed'; ${x}=@{Name='abc'}; ${x}.Name<# unclosed"),
            ("invalid_comment_conditional_sentinel", "$MemberCommentSentinel='changed'; ${x}=@{Name='abc'}; ${x}?.Name<# unclosed"),
            ("finally_return_after_member_comment", "try {'first'} finally {${x}=@{Name='abc'}; ${x}.Name<# gap #>; return 'invalid'}"),
            ("function_name_spaced_comment", "function Function<# gap #> {'value'}; Function"),
            ("invalid_comment_key_dot", "$h=@{.Name<# c #>=1}; $h['.Name']"),
            ("invalid_comment_key_question", "$h=@{Name?<# c #>=1}; $h['Name?']"),
            ("invalid_comment_key_hyphen", "$h=@{Name-Value<# c #>=1}; $h['Name-Value']"),
            ("invalid_comment_type_numeric_suffix", "[int<# gap #>32]1"),
            ("invalid_comment_type_word_suffix", "[int<# gap #>foo]1"),
            ("invalid_comment_type_qualified_suffix", "[System<# gap #>.Math]::PI"),
            ("invalid_comment_type_array_suffix", "[int<# gap #>32[]]@(1,2)"),
            ("invalid_comment_type_generic_prefix", "[System.Collections<# gap #>.Generic.List[int]]@(1,2)"),
            ("invalid_comment_type_parameter", "function Test-Type {param([int<# gap #>32]$v) $v}; Test-Type 1"),
            ("invalid_comment_type_attribute_prefix", "function Test-Type {param([Validate<# gap #>Set('a')]$v) $v}; Test-Type 'a'"),
            ("invalid_comment_type_unterminated", "[int<# gap]1"),
            ("invalid_comment_type_generic_unterminated", "[System.Collections.Generic.List[int<# gap]]@(1,2)"),
            ("invalid_comment_type_bare_parameter", "param([int<# gap #>32]$v=12) $v"),
            ("invalid_comment_type_signature_parameter", "function Test-Type([int<# gap #>32]$v) {$v}; Test-Type 1"),
            ("invalid_comment_type_catch_clause", "try {throw 'boom'} catch [System<# gap #>.Exception] {'caught'}"),
            ("invalid_comment_type_expression_args_cast", "[int<# gap #>(1)]2"),
            ("invalid_comment_type_expression_args_literal", "[int<# gap #>(1)]"),
            ("invalid_comment_type_expression_plain_args_cast", "[int(1)]2"),
            ("invalid_comment_type_expression_plain_args_literal", "[int(1)]"),
            ("invalid_comment_type_attribute_word_suffix", "[int<# gap #>(1)Foo]$v"),
            ("invalid_comment_type_attribute_second_arguments", "[int<# gap #>(1)(2)]$v"),
            ("invalid_comment_type_attribute_array_suffix", "[int<# gap #>(1)[]]$v"),
            ("invalid_comment_type_attribute_comma_suffix", "[int<# gap #>(1),int]$v"),
            ("invalid_comment_type_generic_trailing_comma", "[System.Collections.Generic.List[int<#gap#>,]]1"),
            ("invalid_comment_type_array_rank_word", "[int<#gap#>[,Foo]]1"),
            ("invalid_comment_type_newline_before_close", "[int<#gap#>\n]1"),
            ("invalid_comment_type_newline_before_array", "[int<#gap#>\n[]]@(1,2)"),
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
        Console.WriteLine($"Member-comment output scenarios: {cases.Length - outputFailures}/{cases.Length} passed.");
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
        Console.WriteLine($"Member-comment diagnostics: {diagnostics.Length - (failures.Count - outputFailures)}/{diagnostics.Length} passed.");
        await VerifyParseBeforeExecutionAsync();
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static void VerifyPublicTokenCompatibility()
    {
        var legacy = new PowerShellWasmToken(PowerShellWasmTokenKind.Identifier, "sample", 2, 6, true);
        var (kind, text, offset, length, whitespace) = legacy;
        var edited = legacy with { Offset = 5 };
        if (kind != PowerShellWasmTokenKind.Identifier || text != "sample" || offset != 2 || length != 6 ||
            !whitespace || edited.Offset != 5 || edited.Text != "sample")
            throw new InvalidOperationException("Public token construction, deconstruction and with-expression edits must remain compatible.");

        const string compactSource = "Write-Output prefix<#c#>suffix .Name<#c#>";
        var compact = PowerShellWasmTokenizer.Tokenize(compactSource)
            .Where(static token => token.Kind != PowerShellWasmTokenKind.EndOfInput).ToArray();
        if (!compact.Select(static token => token.Text).SequenceEqual(["Write-Output", "prefix<#c#>suffix", ".Name<#c#>"]) ||
            compact.Any(token => compactSource.Substring(token.Offset, token.Length) != token.Text))
            throw new InvalidOperationException("Public tokenization must retain generic comment delimiters and original source spans.");

        var spaced = PowerShellWasmTokenizer.Tokenize("Write-Output prefix<# gap #>suffix; 'after'")
            .Where(static token => token.Kind != PowerShellWasmTokenKind.EndOfInput);
        if (!spaced.Select(static token => token.Text).SequenceEqual(["Write-Output", "prefix<#", "gap"]))
            throw new InvalidOperationException("Public generic tokenization must not turn embedded delimiters into block comments.");
        Console.WriteLine("Member-comment public token compatibility: 3/3 passed.");
    }

    private static void VerifyCommentedTypeParseCompatibility()
    {
        // Native type/attribute syntax acceptance is separate from runtime type resolution and attribute support.
        string[] scripts =
        [
            "[int(1)]$v",
            "[int<#gap#>(1)]$v",
            "[int(1)][string]$v",
            "([int(1)]$v)",
            "[int(1)]${v}=2",
            "[ValidateSet<#gap#>('a','b')]$v='a';$v",
            "[int<#gap#>[Foo+4]]1",
        ];
        foreach (var script in scripts) _ = new PowerShellWasmParser().Parse(script);
        Console.WriteLine("Member-comment type parser compatibility: 7/7 passed.");
    }

    private static async ValueTask VerifyParseBeforeExecutionAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        await runtime.ExecuteAsync("$MemberCommentSentinel = 'unchanged'");
        string[] scripts =
        [
            "$MemberCommentSentinel='changed'; ${x}='abc'; ${x}.Substring<# gap #>(1)",
            "$MemberCommentSentinel='changed'; ${x}=@{Name='abc'}; ${x}.Name<# unclosed",
            "$MemberCommentSentinel='changed'; ${x}=@{Name='abc'}; ${x}?.Name<# unclosed",
            "$MemberCommentSentinel='changed'; $h=@{.Name<# c #>=1}; $h['.Name']",
            "$MemberCommentSentinel='changed'; $h=@{Name?<# c #>=1}; $h['Name?']",
            "$MemberCommentSentinel='changed'; $h=@{Name-Value<# c #>=1}; $h['Name-Value']",
            "$MemberCommentSentinel='changed'; [int<# gap #>32]1",
            "$MemberCommentSentinel='changed'; [int<# gap #>foo]1",
            "$MemberCommentSentinel='changed'; [System<# gap #>.Math]::PI",
            "$MemberCommentSentinel='changed'; [int<# gap #>32[]]@(1,2)",
            "$MemberCommentSentinel='changed'; [System.Collections<# gap #>.Generic.List[int]]@(1,2)",
            "$MemberCommentSentinel='changed'; function Test-Type {param([int<# gap #>32]$v) $v}; Test-Type 1",
            "$MemberCommentSentinel='changed'; function Test-Type {param([Validate<# gap #>Set('a')]$v) $v}; Test-Type 'a'",
            "$MemberCommentSentinel='changed'; [int<# gap]1",
            "$MemberCommentSentinel='changed'; [System.Collections.Generic.List[int<# gap]]@(1,2)",
            "$MemberCommentSentinel='changed'; & {param([int<# gap #>32]$v=12) $v}",
            "$MemberCommentSentinel='changed'; function Test-Type([int<# gap #>32]$v) {$v}; Test-Type 1",
            "$MemberCommentSentinel='changed'; try {throw 'boom'} catch [System<# gap #>.Exception] {'caught'}",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1)]2",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1)]",
            "$MemberCommentSentinel='changed'; [int(1)]2",
            "$MemberCommentSentinel='changed'; [int(1)]",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1)Foo]$v",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1)(2)]$v",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1)[]]$v",
            "$MemberCommentSentinel='changed'; [int<# gap #>(1),int]$v",
            "$MemberCommentSentinel='changed'; [System.Collections.Generic.List[int<#gap#>,]]1",
            "$MemberCommentSentinel='changed'; [int<#gap#>[,Foo]]1",
            "$MemberCommentSentinel='changed'; [int<#gap#>\n]1",
            "$MemberCommentSentinel='changed'; [int<#gap#>\n[]]@(1,2)",
        ];
        foreach (var script in scripts)
        {
            var rejected = false;
            try { await runtime.ExecuteAsync(script); }
            catch (InvalidOperationException) { rejected = true; }
            var result = await runtime.ExecuteAsync("$MemberCommentSentinel");
            if (!rejected || !result.Records.SequenceEqual([new PowerShellWasmOutputRecord("Output", "unchanged")]))
                throw new InvalidOperationException("Invalid member/comment syntax must reject the whole script before execution.");
        }
        Console.WriteLine("Member-comment parse-before-execution: 30/30 passed.");
    }
}
