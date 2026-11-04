using PSWasm;

internal static class StringParsingVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Output records and rejected syntax were independently recorded with native PowerShell 7.6.1.
        // The parser exposes messages rather than native diagnostic IDs, so rejection checks use diagnostic families.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("string_single_doubled", """"""""
'a''b'
"""""""", [new("Output", "a'b")]),
            ("string_double_doubled", """"""""
"a""b"
"""""""", [new("Output", "a\"b")]),
            ("string_double_quote_only", """"""""
""""
"""""""", [new("Output", "\"")]),
            ("string_nested_double", """"""""
"outer $("inner") end"
"""""""", [new("Output", "outer inner end")]),
            ("string_nested_single", """"""""
"outer $('inner') end"
"""""""", [new("Output", "outer inner end")]),
            ("string_nested_recursive", """"""""
"outer $("middle $("inner")") end"
"""""""", [new("Output", "outer middle inner end")]),
            ("string_nested_scriptblock", """"""""
"x $(& { 'inner' }) y"
"""""""", [new("Output", "x inner y")]),
            ("string_nested_hashtable", """"""""
"x $(@{Name='value'}.Name) y"
"""""""", [new("Output", "x value y")]),
            ("string_nested_array_output", """"""""
$OFS=',';"x $(1;2;3) y"
"""""""", [new("Output", "x 1,2,3 y")]),
            ("string_nested_literal_dollar", """"""""
$x='value';"outer $("`$x") real=$x"
"""""""", [new("Output", "outer $x real=value")]),
            ("string_escaped_dollar", """"""""
$x='value';"`$x ${x} `$($x)"
"""""""", [new("Output", "$x value $(value)")]),
            ("string_escaped_backtick", """"""""
$x='v';"a``b $x"
"""""""", [new("Output", "a`b v")]),
            ("string_standard_escapes", """"""""
$s="`0`a`b`e`f`n`r`t`v";([char[]]$s | ForEach-Object {[int]$_}) -join ','
"""""""", [new("Output", "0,7,8,27,12,10,13,9,11")]),
            ("string_unknown_escape", """"""""
"a`qb"
"""""""", [new("Output", "aqb")]),
            ("string_unicode_escape_bmp", """"""""
$s="`u{41}`u{03A9}";"value=$s;length=$($s.Length)"
"""""""", [new("Output", "value=AΩ;length=2")]),
            ("string_unicode_escape_surrogate", """"""""
$s="`u{1F600}";"length=$($s.Length);first=$([int][char]$s[0]);last=$([int][char]$s[1])"
"""""""", [new("Output", "length=2;first=55357;last=56832")]),
            ("string_single_literal_backtick", """"""""
'a`nb$x'
"""""""", [new("Output", "a`nb$x")]),
            ("string_braced_variable_spaces", """"""""
${a b}='value';"x ${a b} y"
"""""""", [new("Output", "x value y")]),
            ("string_braced_variable_suffix", """"""""
$x='value';"${x}suffix"
"""""""", [new("Output", "valuesuffix")]),
            ("string_braced_variable_escaped_brace", """"""""
${a`}b}='value';"x ${a`}b} y"
"""""""", [new("Output", "x value y")]),
            ("string_braced_variable_double_quote", """"""""
${a"b}='value';"x ${a""b} y"
"""""""", [new("Output", "x value y")]),
            ("string_empty_subexpression", """"""""
"a$()b"
"""""""", [new("Output", "ab")]),
            ("string_subexpression_assignment", """"""""
$x=1;"x=$($x=2; $x)";"after=$x"
"""""""", [new("Output", "x=2"), new("Output", "after=2")]),
            ("string_here_literal", """"""""
$s=@'
a"b'$x`n
'@
$s
"""""""", [new("Output", "a\"b'$x`n")]),
            ("string_here_expandable", """"""""
$x='value';$s=@"
a"b'$x
"@
$s
"""""""", [new("Output", "a\"b'value")]),
            ("string_here_nested", """"""""
$s=@"
x $("inner") y
"@
$s
"""""""", [new("Output", "x inner y")]),
            ("string_here_final_newline", """"""""
$s=@'
first
second
'@
"length=$($s.Length);last=$([int][char]$s[-1])"
"""""""", [new("Output", "length=12;last=100")]),
            ("string_argument_quote_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre"middle"post
"""""""", [new("Output", "count=1;value=premiddlepost")]),
            ("string_argument_single_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre'middle'post
"""""""", [new("Output", "count=1;value=premiddlepost")]),
            ("string_argument_mixed_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args 'a'"b"c
"""""""", [new("Output", "count=3;value=a|b|c")]),
            ("string_argument_variable_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='v';Show-Args pre${x}post
"""""""", [new("Output", "count=1;value=prevpost")]),
            ("string_argument_subexpression_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre$(1+2)post
"""""""", [new("Output", "count=1;value=pre3post")]),
            ("string_argument_quoted_subexpression_fragments", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args 'pre'$(1+2)'post'
"""""""", [new("Output", "count=3;value=pre|3|post")]),
            ("string_argument_escaped_space", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a` b
"""""""", [new("Output", "count=1;value=a b")]),
            ("string_argument_escaped_metacharacters", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a`;b a`|b a`(b a`)b
"""""""", [new("Output", "count=4;value=a;b|a|b|a(b|a)b")]),
            ("string_argument_variable_shape", """"""""
function Show-Args {"count=$($args.Count);firstArray=$($args[0] -is [array]);first=$($args[0] -join ',')"};$x=1,2;Show-Args $x
"""""""", [new("Output", "count=1;firstArray=True;first=1,2")]),
            ("string_argument_composite_shape", """"""""
function Show-Args {"count=$($args.Count);firstArray=$($args[0] -is [array]);first=$($args[0])"};$x=1,2;$OFS=',';Show-Args pre${x}post
"""""""", [new("Output", "count=1;firstArray=False;first=pre1,2post")]),
            ("string_quoted_command_call", """"""""
& 'Write-Output' 'value'
"""""""", [new("Output", "value")]),
            ("string_quoted_command_expression", """"""""
'Write-Output'
"""""""", [new("Output", "Write-Output")]),
            ("string_quoted_function_name", """"""""
function Show-Value {'result'};& "Show-Value"
"""""""", [new("Output", "result")]),
            ("string_multiline_double", """"""""
$s="first
second";"length=$($s.Length);value=$($s -replace "`n",'|')"
"""""""", [new("Output", "length=12;value=first|second")]),
            ("string_backtick_line_string", """"""""
$s="a`
b";"length=$($s.Length);codes=$(([char[]]$s | ForEach-Object {[int]$_}) -join ',')"
"""""""", [new("Output", "length=3;codes=97,10,98")]),
            ("string_smart_single", """"""""
‘alpha’
"""""""", [new("Output", "alpha")]),
            ("string_smart_double", """"""""
$x='value';“alpha $x”
"""""""", [new("Output", "alpha value")]),
            ("string_smart_mixed_single", """"""""
'alpha’
"""""""", [new("Output", "alpha")]),
            ("string_smart_mixed_double", """"""""
"alpha”
"""""""", [new("Output", "alpha")]),
            ("string_smart_doubled_single", """"""""
‘a’’b’
"""""""", [new("Output", "a’b")]),
            ("string_smart_doubled_double", """"""""
“a”“b”
"""""""", [new("Output", "a“b")]),
            ("string_ordinary_smart_embedded_single", """"""""
'a‘’b'
"""""""", [new("Output", "a’b")]),
            ("string_ordinary_smart_embedded_double", """"""""
"a“”b"
"""""""", [new("Output", "a”b")]),
            ("string_nested_double_backtick_escape", """"""""
"x $("a``"b") y"
"""""""", [new("Output", "x a\"b y")]),
            ("string_nested_doubled_quote", """"""""
"x $("a""""b") y"
"""""""", [new("Output", "x a\"b y")]),
            ("string_nested_paired_quoted_parens", """"""""
"x $('(a)') y"
"""""""", [new("Output", "x (a) y")]),
            ("string_nested_comment_balanced", """"""""
"x $(1 <# (balanced) #>) y"
"""""""", [new("Output", "x 1 y")]),
            ("string_here_backtick_nested", """"""""
$s=@"
x $("a`"b") y
"@
$s
"""""""", [new("Output", "x a\"b y")]),
            ("string_here_smart_delimiters", """"""""
$x='value';$s=@“
$x
”@
$s
"""""""", [new("Output", "value")]),
            ("string_here_header_spaces", """"""""
$s=@' 	
value
'@
$s
"""""""", [new("Output", "value")]),
            ("string_here_doubled_double_literal", """"""""
$s=@"
a""b
"@
$s
"""""""", [new("Output", "a\"\"b")]),
            ("string_unicode_zero_and_max", """"""""
$s="`u{0}`u{10ffff}";"length=$($s.Length);codes=$(([char[]]$s | ForEach-Object {[int]$_}) -join ',')"
"""""""", [new("Output", "length=3;codes=0,56319,57343")]),
            ("string_unicode_escaped_literal", """"""""
"``u{41}"
"""""""", [new("Output", "`u{41}")]),
            ("string_argument_quote_then_bare", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args "a"b
"""""""", [new("Output", "count=2;value=a|b")]),
            ("string_argument_variable_then_quote", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';Show-Args $x"b"
"""""""", [new("Output", "count=1;value=ab")]),
            ("string_argument_variable_then_single", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';Show-Args $x'b'
"""""""", [new("Output", "count=1;value=ab")]),
            ("string_argument_subexpression_then_bare", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args $(1)b
"""""""", [new("Output", "count=2;value=1|b")]),
            ("string_argument_parenthesis_then_bare", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args (1)b
"""""""", [new("Output", "count=2;value=1|b")]),
            ("string_argument_bare_single_double", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a'b'"c"d
"""""""", [new("Output", "count=1;value=abcd")]),
            ("string_argument_braced_lead", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';Show-Args ${x}b
"""""""", [new("Output", "count=1;value=ab")]),
            ("string_argument_bare_dollar_escape", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';Show-Args pre`$x
"""""""", [new("Output", "count=1;value=pre$x")]),
            ("string_argument_bare_backtick_escape", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a``b
"""""""", [new("Output", "count=1;value=a`b")]),
            ("string_argument_quoted_empty", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args "" ''
"""""""", [new("Output", "count=2;value=|")]),
            ("string_argument_bare_empty_fragment", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre""post
"""""""", [new("Output", "count=1;value=prepost")]),
            ("string_argument_comma_separation", """"""""
function Show-Args {"count=$($args.Count);firstArray=$($args[0] -is [array]);first=$($args[0] -join '|');second=$($args[1])"};Show-Args a"b",c"d" end
"""""""", [new("Output", "count=2;firstArray=True;first=ab|cd;second=end")]),
            ("string_argument_bare_nested_quotes", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a$("b")c
"""""""", [new("Output", "count=1;value=abc")]),
            ("string_argument_leading_subexpression_quoted", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args $("a")"b"
"""""""", [new("Output", "count=2;value=a|b")]),
            ("string_argument_boundary_evaluation", """"""""
$i=0;function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre$(++$i)post$(++$i);"after=$i"
"""""""", [new("Output", "count=1;value=prepost"), new("Output", "after=2")]),
            ("string_argument_line_continuation", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a`
b
"""""""", [new("Output", "count=1;value=a\nb")]),
            ("string_fragment_command_name", """"""""
Wr"ite"-Output 'value'
"""""""", [new("Output", "value")]),
            ("string_fragment_function_command_name", """"""""
function Show-Value {'result'};Show-'Value'
"""""""", [new("Output", "result")]),
            ("string_smart_low_single", """"""""
‚alpha‛
"""""""", [new("Output", "alpha")]),
            ("string_smart_low_double", """"""""
„alpha”
"""""""", [new("Output", "alpha")]),
            ("string_unicode_dollar_literal", """"""""
$x='expanded';"`u{24}x"
"""""""", [new("Output", "$x")]),
            ("string_argument_variable_operator", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x=3;Show-Args $x+2
"""""""", [new("Output", "count=1;value=3+2")]),
            ("string_argument_member_suffix", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x=[pscustomobject]@{Name='a'};Show-Args $x.Name-more
"""""""", [new("Output", "count=2;value=a|-more")]),
            ("string_argument_index_suffix", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='abc';Show-Args $x[0]suffix
"""""""", [new("Output", "count=2;value=a|suffix")]),
            ("string_argument_array_subexpression_suffix", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args @(1,2)suffix
"""""""", [new("Output", "count=2;value=System.Object[]|suffix")]),
            ("string_argument_bare_array_subexpression", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre@(1,2)post
"""""""", [new("Output", "count=3;value=pre@|System.Object[]|post")]),
            ("string_argument_bare_hashtable", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre@{Name=1}post
"""""""", [new("Output", "count=3;value=pre@|Name=1|post")]),
            ("string_argument_spaced_arithmetic", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args 1 + 2
"""""""", [new("Output", "count=3;value=1|+|2")]),
            ("string_argument_unspaced_arithmetic", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args 1+2
"""""""", [new("Output", "count=1;value=1+2")]),
            ("string_argument_type_literal", """"""""
function Show-Args {"count=$($args.Count);first=$($args[0]);type=$($args[0].GetType().Name)"};Show-Args [int]
"""""""", [new("Output", "count=1;first=[int];type=String")]),
            ("string_argument_cast_literal", """"""""
function Show-Args {"count=$($args.Count);first=$($args[0]);type=$($args[0].GetType().Name)"};Show-Args [int]1
"""""""", [new("Output", "count=1;first=[int]1;type=String")]),
            ("string_argument_bare_comment", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a#b c
"""""""", [new("Output", "count=2;value=a#b|c")]),
            ("string_argument_standalone_comment", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a #comment
Show-Args b
"""""""", [new("Output", "count=1;value=a"), new("Output", "count=1;value=b")]),
            ("string_argument_standalone_line_continuation", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a `
b
"""""""", [new("Output", "count=2;value=a|b")]),
            ("string_argument_escaped_number", """"""""
function Show-Args {"count=$($args.Count);first=$($args[0]);type=$($args[0].GetType().Name)"};Show-Args `1
"""""""", [new("Output", "count=1;first=1;type=String")]),
            ("string_argument_bare_unicode_escape", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a`u{41}b
"""""""", [new("Output", "count=1;value=aAb")]),
            ("string_argument_trailing_backtick", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a`
"""""""", [new("Output", "count=1;value=a`")]),
            ("string_here_escaped_dollar", """"""""
$x='value';$s=@"
`$x `u{24}x $x
"@
$s
"""""""", [new("Output", "$x $x value")]),
            ("string_argument_escaped_keywords", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args fa`lse nu`ll
"""""""", [new("Output", "count=2;value=false|null")]),
            ("string_argument_fragment_evaluation_order", """"""""
$i=0;function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args pre$((++$i))post$((++$i));"after=$i"
"""""""", [new("Output", "count=1;value=pre1post2"), new("Output", "after=2")]),
            ("string_argument_numeric_types", """"""""
function Show-Args {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};Show-Args 2 -2 +2
"""""""", [new("Output", "value=2;type=Int32"), new("Output", "value=-2;type=String"), new("Output", "value=+2;type=String")]),
            ("string_argument_spaced_operator_types", """"""""
function Show-Args {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};Show-Args 1 + 2
"""""""", [new("Output", "value=1;type=Int32"), new("Output", "value=+;type=String"), new("Output", "value=2;type=Int32")]),
            ("string_argument_signed_spaced_types", """"""""
function Show-Args {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};Show-Args + 2 - 2
"""""""", [new("Output", "value=+;type=String"), new("Output", "value=2;type=Int32"), new("Output", "value=-;type=String"), new("Output", "value=2;type=Int32")]),
            ("string_review_escaped_keyword_while", """"""""
function while {'called'};whi`le
"""""""", [new("Output", "called")]),
            ("string_review_escaped_keyword_throw", """"""""
function throw {'called'};t`hrow
"""""""", [new("Output", "called")]),
            ("string_review_parameter_quoted_suffix", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args -a"b"
"""""""", [new("Output", "count=1;value=-ab")]),
            ("string_review_parameter_declared_quoted_suffix", """"""""
function Show-Args {param($a,$ab)"a=$a;ab=$ab;rest=$($args -join '|')"};Show-Args -a"b" c
"""""""", [new("Output", "a=-ab;ab=c;rest=")]),
            ("string_review_hash_after_fragment", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a"b"#c end
"""""""", [new("Output", "count=2;value=ab#c|end")]),
            ("string_review_hash_after_quote", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args "a"#b end
"""""""", [new("Output", "count=1;value=a")]),
            ("string_review_hash_after_variable", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='v';Show-Args $x#b end
"""""""", [new("Output", "count=2;value=v#b|end")]),
            ("string_review_fused_variables", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';$y='b';Show-Args pre$x$y-post
"""""""", [new("Output", "count=1;value=preab-post")]),
            ("string_review_fused_variable_boundaries", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x='a';$xpost='whole';Show-Args $xpost ${x}post pre$xpost
"""""""", [new("Output", "count=3;value=whole|apost|prewhole")]),
            ("string_review_ofs_custom_objects", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$x=[pscustomobject]@{Name='a'};$y=[pscustomobject]@{Name='b'};$OFS=',';$a=@($x,$y);Show-Args pre${a}post
"""""""", [new("Output", "count=1;value=pre,post")]),
            ("string_review_ofs_nested_arrays", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};$a=@(@(1,2),@(3,4));$OFS='|';Show-Args pre${a}post
"""""""", [new("Output", "count=1;value=preSystem.Object[]|System.Object[]post")]),
            ("string_review_literal_command_subexpression", """"""""
function a`$`(2`) {'called'};a$(2)
"""""""", [new("Output", "called")]),
            ("string_review_literal_command_variable", """"""""
function a`$x {'called'};$x='value';a$x
"""""""", [new("Output", "called")]),
            ("string_review_custom_argument_fragment", """"""""
function Show-Args {"value=$($args[0])"};$x=[pscustomobject]@{Name='a'};Show-Args pre${x}post
"""""""", [new("Output", "value=pre@{Name=a}post")]),
            ("string_review_custom_scalar_array_interpolation", """"""""
$x=[pscustomobject]@{Name='a'};$a=,$x;"scalar=$x;array=$a"
"""""""", [new("Output", "scalar=@{Name=a};array=")]),
            // Preserve the exact CRLF pair independently of this source file's line endings.
            ("string_argument_interior_crlf", "function Show-Args {$v=[string]$args[0];\"count=$($args.Count);length=$($v.Length);codes=$(([char[]]$v | ForEach-Object {[int]$_}) -join ',')\"};function After-Line {'after'};Show-Args a`\r\nAfter-Line", [new("Output", "count=1;length=2;codes=97,13"), new("Output", "after")]),
        };
        var diagnostics = new (string Name, string Script, string NativeIds, string ErrorPattern)[]
        {
            ("string_invalid_unterminated_single", """"""""
'abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_double", """"""""
"abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_escaped_double", """"""""
"abc`"
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_doubled_double", """"""""
"abc""
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_single_doubled", """"""""
'abc''
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_here_single", """"""""
@'
abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_unterminated_here_double", """"""""
@"
abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_header_characters", """"""""
@"abc
"@
"""""""", "UnexpectedCharactersAfterHereStringHeader,TerminatorExpectedAtEndOfString", "(here-string|header|end of.*line|terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_header_missing_newline", """"""""
@'
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_footer_indented", """"""""
@'
abc
 '@
"""""""", "WhitespaceBeforeHereStringFooter", "(here-string|terminator|white.?space)"),
            ("string_invalid_unclosed_subexpression", """"""""
"abc $(1+2"
"""""""", "IncompleteDollarSubexpressionReference,TerminatorExpectedAtEndOfString,TerminatorExpectedAtEndOfString,ExpectedValueExpression,UnexpectedToken,MissingEndParenthesisInSubexpression", "(subexpression|parenthes|RParen|terminator|terminator|unterminated|closing.*quote|unexpected|unrecognized|expected.*token|remaining|expected.*expression)"),
            ("string_invalid_unclosed_nested_string", """"""""
"abc $("inner) end"
"""""""", "TerminatorExpectedAtEndOfString,MissingEndParenthesisInSubexpression", "(terminator|unterminated|closing.*quote|subexpression|parenthes|RParen|terminator)"),
            ("string_invalid_unclosed_scriptblock", """"""""
"abc $(& { 'x') end"
"""""""", "MissingEndCurlyBrace", "(closing.*brace|RBrace|curly|script.?block)"),
            ("string_invalid_unclosed_braced_variable", """"""""
"${name"
"""""""", "IncompleteDollarVariableReference,TerminatorExpectedAtEndOfString", "(variable|closing.*brace|terminator|unterminated|closing.*quote)"),
            ("string_invalid_empty_braced_variable", """"""""
"${}"
"""""""", "EmptyVariableReference", "(variable|closing.*brace)"),
            ("string_invalid_invalid_variable_drive", """"""""
"$x:"
"""""""", "InvalidVariableReferenceWithDrive", "(variable|closing.*brace)"),
            ("string_invalid_invalid_unicode_hex", """"""""
"`u{zz}"
"""""""", "InvalidUnicodeEscapeSequence", "(unicode|escape)"),
            ("string_invalid_invalid_unicode_missing_brace", """"""""
"`u1234"
"""""""", "InvalidUnicodeEscapeSequence", "(unicode|escape)"),
            ("string_invalid_invalid_unicode_range", """"""""
"`u{110000}"
"""""""", "InvalidUnicodeEscapeSequenceValue", "(unicode|escape)"),
            ("string_invalid_quoted_command_without_call", """"""""
'Write-Output' 'value'
"""""""", "UnexpectedToken", "(unexpected|unrecognized|expected.*token|remaining|expected.*expression)"),
            ("string_invalid_expression_mixed_quotes", """"""""
$x='a'"b"
"""""""", "UnexpectedToken", "(unexpected|unrecognized|expected.*token|remaining|expected.*expression)"),
            ("string_invalid_unterminated_argument", """"""""
Write-Output pre"middle
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_nested_quoted_parenthesis", """"""""
"x $(')') y"
"""""""", "TerminatorExpectedAtEndOfString,MissingEndParenthesisInSubexpression", "(terminator|unterminated|closing.*quote|subexpression|parenthes|RParen|terminator)"),
            ("string_invalid_nested_escaped_quote", """"""""
"x $("a`"b") y"
"""""""", "TerminatorExpectedAtEndOfString,UnexpectedToken,MissingEndParenthesisInSubexpression", "(terminator|unterminated|closing.*quote|unexpected|unrecognized|expected.*token|remaining|expected.*expression|subexpression|parenthes|RParen|terminator)"),
            ("string_invalid_unicode_empty", """"""""
"`u{}"
"""""""", "InvalidUnicodeEscapeSequence", "(unicode|escape)"),
            ("string_invalid_unicode_too_many_digits", """"""""
"`u{0000041}"
"""""""", "TooManyDigitsInUnicodeEscapeSequence", "(unicode|escape)"),
            ("string_invalid_here_terminator_wrong_quote", """"""""
@'
value
"@
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_subexpr_comment_raw_paren", """"""""
"x $(1 <# ) #>) y"
"""""""", "MissingTerminatorMultiLineComment,MissingEndParenthesisInSubexpression", "(comment|terminator|subexpression|parenthes|RParen|terminator)"),
            ("string_invalid_subexpr_quoted_open_paren", """"""""
"x $('(') y"
"""""""", "IncompleteDollarSubexpressionReference,TerminatorExpectedAtEndOfString", "(subexpression|parenthes|RParen|terminator|terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_nested_unclosed_subexpression", """"""""
@"
x $(1+2
"@
"""""""", "IncompleteDollarSubexpressionReference,TerminatorExpectedAtEndOfString,TerminatorExpectedAtEndOfString,MissingEndParenthesisInSubexpression", "(subexpression|parenthes|RParen|terminator|terminator|unterminated|closing.*quote)"),
            ("string_invalid_smart_unterminated", """"""""
“abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_smart_unterminated", """"""""
@“
abc
"""""""", "TerminatorExpectedAtEndOfString", "(terminator|unterminated|closing.*quote)"),
            ("string_invalid_here_nested_quoted_paren", """"""""
$s=@"
x $(')') y
"@
$s
"""""""", "TerminatorExpectedAtEndOfString,MissingEndParenthesisInSubexpression", "(terminator|unterminated|closing.*quote|subexpression|parenthes|RParen|terminator)"),
            ("string_invalid_argument_escaped_keyword", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args tr`ue nu`ll
"""""""", "InvalidUnicodeEscapeSequence", "(unicode|escape)"),
            ("string_review_initial_comma", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args ,a
"""""""", "MissingArgument", "(argument|expression|comma)"),
            ("string_review_consecutive_comma", """"""""
function Show-Args {"count=$($args.Count);value=$($args -join '|')"};Show-Args a,,b
"""""""", "MissingExpression,MissingArgument", "(argument|expression|comma)"),
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
        Console.WriteLine($"String parsing scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        var outputFailures = failures.Count;
        foreach (var (name, script, nativeIds, pattern) in diagnostics)
        {
            Exception? failure = null;
            try { new PSWasm.Language.PowerShellWasmParser().Parse(script); }
            catch (Exception exception) { failure = exception; }
            if (failure is not InvalidOperationException ||
                !System.Text.RegularExpressions.Regex.IsMatch(failure.Message, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                failures.Add($"{name}: expected parse rejection matching {pattern} (native: {nativeIds}); " +
                    $"received {failure?.GetType().Name ?? "no exception"}: {failure?.Message}");
            }
        }
        Console.WriteLine($"String parse diagnostics: {diagnostics.Length - (failures.Count - outputFailures)}/{diagnostics.Length} passed.");
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures));
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
