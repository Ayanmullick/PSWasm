using PSWasm;
using PSWasm.Language;

internal static class ParenthesizedExpressionVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell distinguishes a parenthesized pipeline from a $()/@() statement list.
        // Behavioral guidance: Parser.ParenthesizedExpressionRule and Parser.SubExpressionRule.
        // https://github.com/PowerShell/PowerShell/blob/v7.5.2/src/System.Management.Automation/engine/parser/Parser.cs
        foreach (var script in new[]
        {
            "()", "(\n)", "(1;)", "(;1)", "(1;2)", "(1\n2)", "(1\n+ 2)",
            "(Write-Output 'a'; Write-Output 'b')", "(Write-Output 'a'\nWrite-Output 'b')",
            "($x in 1,2)", "(foreach ($x in 1,2) {$x})", "(for ($x=0;$x -lt 1;$x++) {$x})"
        })
        {
            try
            {
                _ = new PowerShellWasmParser().Parse(script);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            throw new InvalidOperationException($"Expected a parse error for parenthesized input: {script}");
        }

        var cases = new (string Script, string[] Expected)[]
        {
            ("(1 + 2) * 3; ((Write-Output ' abc ')).Trim()", ["9", "abc"]),
            ("($x = 3); $x; ($x += 2); $x", ["3", "3", "5", "5"]),
            ("($x = Write-Output 'assigned'); $x; ($y = if ($true) { 4 }); $y", ["assigned", "assigned", "4", "4"]),
            ("function Answer { 42 }; (Answer)", ["42"]),
            ("$a = (Write-Output 'a','b'); $a.Count; $a -join ','; (Write-Output 'single') -is [string]", ["2", "a,b", "True"]),
            ("(Write-Output 'a' | Write-Output).ToUpperInvariant(); (Write-Output 'b' && Write-Output 'c') -join ','", ["A", "b,c"]),
            ("(\n1 +\n2\n); (1,\n2).Count", ["3", "2"]),
            ("(Write-Output 'a' |\nWrite-Output); ('b'\n| Write-Output)", ["a", "b"]),
            ("$(Write-Output 'a'; Write-Output 'b') -join ','; @(Write-Output 'a'; Write-Output 'b').Count", ["a,b", "2"]),
            ("$($v = 1; $v + 1); @(if ($true) { 3 }; 4).Count", ["2", "2"]),
            ("(Write-Output $(1;2)) -join ','; (Write-Output @{a=1; b=2}).Count", ["1,2", "2"])
        };

        foreach (var (script, expected) in cases)
        {
            PowerShellWasmResult result;
            try
            {
                result = await new PowerShellWasmRuntime().ExecuteAsync(script);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Parenthesized expression failed for {script}", error);
            }
            if (!result.Output.SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Parenthesized expression mismatch for {script}\nExpected: {string.Join(" | ", expected)}\nActual: {result.Text}");
            }
        }

        // Here these words are command names, not executable control-flow statements.
        foreach (var (script, commandName) in new[]
        {
            ("(if ($true) {'yes'})", "if"), ("(while ($false) {1})", "while"),
            ("(try {1} catch {2})", "try"), ("(return 1)", "return")
        })
        {
            var parsed = new PowerShellWasmParser().Parse(script);
            if (parsed.Statements.Single() is not ExpressionStatementAst
                {
                    Expression: ParenthesizedExpressionAst
                    {
                        Expression: StatementExpressionAst { Statement: CommandStatementAst command }
                    }
                } || command.Command.Name != commandName)
            {
                throw new InvalidOperationException($"Expected the parenthesized command '{commandName}': {script}");
            }
        }
    }
}
