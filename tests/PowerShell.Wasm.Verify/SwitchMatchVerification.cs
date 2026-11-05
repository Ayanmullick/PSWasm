using PSWasm;

internal static class SwitchMatchVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell supplied these expectations, including last-option precedence and case sensitivity.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/parser/Compiler.cs#L4578-L4603
        var cases = new (string Name, string Script, string[] Expected)[]
        {
            ("default_exact", "switch ('cat') { 'c*' {'wild'}; 'cat' {'exact'} }", ["exact"]),
            ("default_literal_metacharacters",
                "switch (@('c*','c?','[a]')) { 'c*' {'star'}; 'c?' {'question'}; '[a]' {'brackets'} }",
                ["star", "question", "brackets"]),
            ("default_case_insensitive", "switch ('Cat') { 'cat' {'match'}; default {'miss'} }", ["match"]),
            ("default_case_sensitive", "switch -CaseSensitive ('Cat') { 'cat' {'bad'}; 'Cat' {'match'} }", ["match"]),
            ("explicit_exact", "switch -Exact ('cat') { 'c*' {'bad'}; 'cat' {'match'} }", ["match"]),
            ("explicit_exact_case_insensitive", "switch -Exact ('Cat') { 'cat' {'match'}; default {'miss'} }", ["match"]),
            ("explicit_wildcard",
                "switch -Wildcard ('cat') { 'c*' {'star'}; 'c?t' {'question'}; 'ca[t]' {'brackets'} }",
                ["star", "question", "brackets"]),
            ("wildcard_case_insensitive", "switch -Wildcard ('Cat') { 'c*' {'match'}; default {'miss'} }", ["match"]),
            ("wildcard_case_sensitive", "switch -Wildcard -CaseSensitive ('Cat') { 'c*' {'bad'}; 'C*' {'match'} }", ["match"]),
            ("regex_case_insensitive", "switch -Regex ('Cat') { '^c.t$' {'match'}; default {'miss'} }", ["match"]),
            ("regex_case_sensitive", "switch -Regex -CaseSensitive ('Cat') { '^c.t$' {'bad'}; '^C.t$' {'match'} }", ["match"]),
            ("default_multiple_exact_matches", "switch ('cat') { 'cat' {'first'}; 'CAT' {'second'}; default {'bad'} }", ["first", "second"]),
            ("default_no_match", "switch ('cat') { 'c*' {'bad'}; default {'fallback'} }", ["fallback"]),
            ("case_before_exact", "switch -CaseSensitive -Exact ('Cat') { 'cat' {'bad'}; 'Cat' {'match'} }", ["match"]),
            ("case_before_wildcard", "switch -CaseSensitive -Wildcard ('Cat') { 'c*' {'bad'}; 'C*' {'match'} }", ["match"]),
            ("case_before_regex", "switch -CaseSensitive -Regex ('Cat') { '^c.t$' {'bad'}; '^C.t$' {'match'} }", ["match"]),
            ("precedence_exact_wildcard", "switch -Exact -Wildcard ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["wild", "exact"]),
            ("precedence_exact_regex", "switch -Exact -Regex ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["wild", "regex", "exact"]),
            ("precedence_wildcard_exact", "switch -Wildcard -Exact ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["exact"]),
            ("precedence_wildcard_regex", "switch -Wildcard -Regex ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["wild", "regex", "exact"]),
            ("precedence_regex_exact", "switch -Regex -Exact ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["exact"]),
            ("precedence_regex_wildcard", "switch -Regex -Wildcard ('cat') { 'c*t' {'wild'}; 'c.t' {'regex'}; 'cat' {'exact'} }", ["wild", "exact"]),
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
        Console.WriteLine($"Switch matching scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }
}
