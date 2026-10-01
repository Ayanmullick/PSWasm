using PSWasm;

internal static class ArithmeticVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected values cross-checked with PowerShell 7, including operand order and result types.
        var cases = new (string Script, string[] Expected)[]
        {
            ("2 + '3'; '2' + 3; 2 + $null; 2 + ''; 3 * '2'; '2' * 3; 2 * $null", ["5", "23", "2", "2", "6", "222", "0"]),
            ("$n = 2 * '3.5'; $n; $n -is [double]; (2 + '1e2') -is [double]; ([double]2 + 3) -is [double]; ([long]2 + 3) -is [long]; ([byte]2 + 3) -is [int]", ["7", "True", "True", "True", "True", "True"]),
            ("$n = [decimal]1 + [double]2.5; $n; $n -is [decimal]; ([double]1 + [decimal]2.5) -is [decimal]; ([decimal]1 * [double]2.5) -is [decimal]; ([double]1 * [decimal]2.5) -is [decimal]", ["3.5", "True", "True", "True", "True"]),
            ("$n = 2147483647 + 1; $n; $n -is [double]; $n = 50000 * 50000; $n; $n -is [double]; $n = 0 + '9223372036854775807'; $n -is [long]; ($n + 1) -is [double]", ["2147483648", "True", "2500000000", "True", "True", "True"]),
            ("$true + '3.5'; $false + ''; 2 + ' '; 2 + ' 3 '; 2 * ''; 2 + '1,000'", ["4.5", "0", "2", "5", "0", "1002"]),
            ("2 + '0x10'; 2 * '0x10'; 2 + '+0x10'; 2 + '2kb'; 'x' * '0x3'; 0 + '0x1d'; 0 + '0x1b'; 0 + '0x2kb'", ["18", "32", "18", "2050", "xxx", "29", "27", "2048"]),
            ("$n = 0 + '3d'; $n; $n -is [decimal]; (0 + '3l') -is [long]; (0 + '0x1l') -is [long]; $n = 0 + '3.5kb'; $n; $n -is [double]", ["3", "True", "True", "True", "3584", "True"]),
            ("$n = 0 + '2gb'; $n; $n -is [long]; (0 + '1gb') -is [int]; ((0 + '1gb') * 2) -is [double]", ["2147483648", "True", "True", "True"]),
            ("$n = 0 + '0xffffffff'; $n; $n -is [int]; $n = 0 + '0x80000000'; $n; $n -is [int]; $n = 0 + '0x0000000080000000'; $n; $n -is [long]; (0 + '0xffffffffffffffff') -is [long]", ["-1", "True", "-2147483648", "True", "2147483648", "True", "True"]),
            ("$n = 0 + '0b11111111111111111111111111111111'; $n; $n -is [int]; 0 + '0b10000000000000000000000000000000'; 0 + '-0xffffffff'; $n = 0 + '-0x80000000'; $n; $n -is [long]", ["-1", "True", "-2147483648", "1", "2147483648", "True"]),
            ("0 + '0x'; (0 + '0x000000001') -is [int]; (0 + '0x0000000000000001') -is [long]; $n = 0 + '0b000000000000000000000000000000001'; $n; $n -is [int]", ["0", "True", "True", "1", "True"]),
            ("$a = @(1,2) + '3'; $a.Count; $a[2] -is [string]; $a -join ','", ["3", "True", "1,2,3"]),
            ("$a = @(1,2) + $null; $a.Count; $null -eq $a[2]; (@() + $null).Count", ["3", "True", "1"]),
            ("$a = @(1) + (,@(2,3)); $a.Count; $a[1].Count; $a[1][0]; $a[1][1]", ["2", "2", "2", "3"]),
            ("$a = ([byte[]]@(65,66)) + 3; $a.Count; $a -is [object[]]; $a -join ','", ["3", "True", "65,66,3"]),
            ("$a = @(1) + [byte[]]@(65,66); $a.Count; $a -join ','", ["3", "1,65,66"]),
            ("$a = $null + @(1); $a -is [object[]]; $a.Count; ($null + @()).Count; $a = $null * 'x'; $null -eq $a", ["True", "1", "0", "True"]),
            ("'x' + @(1,2); $OFS = '|'; 'x' + @(1,$null,3); 'x' + [byte[]]@(65,66)", ["x1 2", "x1||3", "x65|66"]),
            ("'ab' * 3; ('x' * 0).Length; ('x' * $null).Length; 'x' * 2.5; 'x' * '3.5'", ["ababab", "0", "0", "xx", "xxxx"]),
            ("$a = @(1,2) * 2; $a.Count; $a -join ','; (@(1) * 0).Count; (@() * 3).Count", ["4", "1,2,1,2", "0", "0"]),
            ("$a = ([byte[]]@(65,66)) * 2; $a -is [byte[]]; $a.Length; $a[2]; $a[3]", ["True", "4", "65", "66"]),
            ("$a = ([int[]]@(1,2)) * 2; $a -is [int[]]; $a -join ','; $a = ([string[]]@('a')) * 2; $a -is [string[]]; $a -join ','", ["True", "1,2,1,2", "True", "a,a"]),
            ("$a = ([double[]]@(1,2)) * 2; $a -is [double[]]; $a.Count; $a -join ','", ["True", "4", "1,2,1,2"]),
            ("$a = @{x=1}; $b = @{y=2}; $c = $a + $b; $c.Count; $c.x; $c.y; $a.Count; $b.Count; $c.x=9; $a.x", ["2", "1", "2", "1", "1", "1"]),
            ("$a = 2; $a += '3'; $a; $s = 'ab'; $s *= 2; $s; $a = @(1); $a += $null; $a *= 2; $a.Count; $a -join ','", ["5", "abab", "4", "1,,1,"]),
            ("$h = @{x=1}; try { $h += @{X=2} } catch { 'duplicate' }; $h.Count; $h.x", ["duplicate", "1", "1"])
        };

        foreach (var (script, expected) in cases)
        {
            var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
            if (!result.Output.SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Arithmetic mismatch for {script}\nExpected: {string.Join(" | ", expected)}\nActual: {result.Text}");
            }
        }

        foreach (var script in new[] { "'x' * -1", "@(1) * -1", "@{x=1} + $null", "@{x=1} + 'y'", "@{x=1} + @{X=2}", "2 + 'abc'", "$true * 3", "0 + 'kb'", "0 + '0b'", "0 + '--0x1'" })
        {
            var result = await new PowerShellWasmRuntime().ExecuteAsync($"try {{ {script} }} catch {{ 'arithmetic-error' }}");
            if (!result.Output.SequenceEqual(["arithmetic-error"]))
            {
                throw new InvalidOperationException($"Expected an arithmetic error for {script}, got {result.Text}");
            }
        }
    }
}
