using PSWasm;

internal static class NumericOperatorVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected values and types are cross-checked with native PowerShell, without reflection or numeric formatting assumptions.
        await VerifyAsync("subtraction preserves numeric types", """
$n = 8 - 3; "$n|int=$($n -is [int])"
$n = [long]8 - 3; "$n|long=$($n -is [long])"
$n = [decimal]8 - 3; "$n|decimal=$($n -is [decimal])"
$n = [double]8 - 3; "$n|double=$($n -is [double])"
$n = [byte]8 - [byte]3; "$n|int=$($n -is [int])"
$n = (0 + '9007199254740993') - (0 + '9007199254740992'); "$n|long=$($n -is [long])"
$n = [decimal]'0.3' - [double]0.1; "$n|decimal=$($n -is [decimal])"
$n = [double]0.3 - [decimal]'0.1'; "$n|decimal=$($n -is [decimal])"
""", ["5|int=True", "5|long=True", "5|decimal=True", "5|double=True", "5|int=True", "1|long=True",
            "0.2|decimal=True", "0.2|decimal=True"]);

        await VerifyAsync("division chooses integral or fractional result", """
$n = 8 / 2; "$n|int=$($n -is [int])"
$n = [long]8 / 2; "$n|long=$($n -is [long])"
$n = 7 / 2; "$n|double=$($n -is [double])"
$n = [long]7 / 2; "$n|double=$($n -is [double])"
$n = [decimal]8 / 2; "$n|decimal=$($n -is [decimal])"
$n = [double]8 / 2; "$n|double=$($n -is [double])"
$n = [byte]8 / [byte]2; "$n|int=$($n -is [int])"
$n = -7 / 2; "$n|double=$($n -is [double])"
$n = [decimal]7 / [double]2; "$n|decimal=$($n -is [decimal])"
$n = [double]7 / [decimal]2; "$n|decimal=$($n -is [decimal])"
$n = (0 + '9007199254740993') / 1; ($n -eq (0 + '9007199254740993')); $n -is [long]
""", ["4|int=True", "4|long=True", "3.5|double=True", "3.5|double=True", "4|decimal=True", "4|double=True",
            "4|int=True", "-3.5|double=True", "3.5|decimal=True", "3.5|decimal=True", "True", "True"]);

        await VerifyAsync("remainder preserves type and dividend sign", """
$n = 8 % 3; "$n|int=$($n -is [int])"
$n = [long]8 % 3; "$n|long=$($n -is [long])"
$n = [decimal]8 % 3; "$n|decimal=$($n -is [decimal])"
$n = [double]8 % 3; "$n|double=$($n -is [double])"
$n = [byte]8 % [byte]3; "$n|int=$($n -is [int])"
-7 % 3
7 % -3
-7 % -3
$n = [decimal]'7.5' % [double]2; "$n|decimal=$($n -is [decimal])"
$n = [double]7.5 % [decimal]2; "$n|decimal=$($n -is [decimal])"
$n = (0 + '9007199254740993') % 2; "$n|long=$($n -is [long])"
""", ["2|int=True", "2|long=True", "2|decimal=True", "2|double=True", "2|int=True", "-1", "1", "-1",
            "1.5|decimal=True", "1.5|decimal=True", "1|long=True"]);

        await VerifyAsync("null boolean and numeric string operands", """
$null - 3
$null / 3
$null % 3
3 - $null
$true - 1
$true / 2
$true % 2
3 - $true
3 / $true
3 % $true
'8' - 3
8 - '0x3'
8 - ''
$n = '8.0' / 2; "$n|double=$($n -is [double])"
$n = 8 / '2.0'; "$n|double=$($n -is [double])"
$n = 8 % '3.0'; "$n|double=$($n -is [double])"
$n = '8d' - 3; "$n|decimal=$($n -is [decimal])"
$n = '8l' / 2; "$n|long=$($n -is [long])"
""", ["-3", "0", "0", "3", "0", "0.5", "1", "2", "3", "0", "5", "5", "8",
            "4|double=True", "4|double=True", "2|double=True", "5|decimal=True", "4|long=True"]);

        await VerifyAsync("integer and decimal zero divisors throw", """
try { 1 / 0 } catch { 'int-division' }
try { 1 % 0 } catch { 'int-remainder' }
try { [long]1 / 0 } catch { 'long-division' }
try { [long]1 % 0 } catch { 'long-remainder' }
try { [decimal]1 / 0 } catch { 'decimal-division' }
try { [decimal]1 % 0 } catch { 'decimal-remainder' }
try { 1 / $null } catch { 'null-division' }
try { 1 % $null } catch { 'null-remainder' }
try { 1 / '' } catch { 'string-division' }
try { 1 / $false } catch { 'bool-division' }
try { [decimal]1 / [double]0 } catch { 'mixed-division' }
try { [double]1 % [decimal]0 } catch { 'mixed-remainder' }
""", ["int-division", "int-remainder", "long-division", "long-remainder", "decimal-division", "decimal-remainder",
            "null-division", "null-remainder", "string-division", "bool-division", "mixed-division", "mixed-remainder"]);

        await VerifyAsync("double zero divisors retain floating point behavior", """
$n = [double]1 / 0; $n -is [double]; $n -gt 0; (1 / $n) -eq 0
$n = 1 / [double]0; $n -is [double]; $n -gt 0
$n = [double]-1 / 0; $n -is [double]; $n -lt 0
$n = [double]0 / 0; $n -is [double]; $n -ne $n
$n = [double]1 % 0; $n -is [double]; $n -ne $n
$n = 1 % [double]0; $n -is [double]; $n -ne $n
""", ["True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True"]);

        await VerifyAsync("integer overflow promotion and minimum remainder", """
$intMin = 0 + '-2147483648'
$longMin = 0 + '-9223372036854775808'
$n = $intMin - 1; "$n|double=$($n -is [double])"
$n = $longMin - 1; $n -is [double]; $n -lt 0
$n = $intMin / -1; "$n|double=$($n -is [double])"
$n = $longMin / -1; $n -is [double]; $n -gt 0
$n = $intMin % -1; "$n|int=$($n -is [int])"
$n = $longMin % -1; "$n|long=$($n -is [long])"
$a = 0 + '9223372036854775807'; $b = [long]-1025
$n = $a - $b; $n -eq (0 + '9.223372036854776e18'); $n -is [double]
$product = $a * $a; $product -eq (0 + '8.507059173023461e37'); $product -is [double]
""", ["-2147483649|double=True", "True", "True", "2147483648|double=True", "True", "True", "0|int=True", "0|long=True",
            "True", "True", "True", "True"]);

        await VerifyAsync("unary conversion and type preservation", """
$n = +$null; "$n|int=$($n -is [int])"
$n = -$null; "$n|int=$($n -is [int])"
$n = +$true; "$n|int=$($n -is [int])"
$n = -$true; "$n|int=$($n -is [int])"
$n = +([byte]2); "$n|int=$($n -is [int])"
$n = -([byte]2); "$n|int=$($n -is [int])"
$n = +([long]2); "$n|long=$($n -is [long])"
$n = -([long]2); "$n|long=$($n -is [long])"
$n = +([decimal]'2.5'); "$n|decimal=$($n -is [decimal])"
$n = -([decimal]'2.5'); "$n|decimal=$($n -is [decimal])"
$n = +([double]2); "$n|double=$($n -is [double])"
$n = -([double]2); "$n|double=$($n -is [double])"
$n = +'2'; "$n|int=$($n -is [int])"
$n = -'2.5'; "$n|double=$($n -is [double])"
$n = +''; "$n|int=$($n -is [int])"
$n = +'2147483648'; $n -is [long]
$n = -(0 + '-2147483648'); "$n|double=$($n -is [double])"
$n = -(0 + '-9223372036854775808'); $n -is [double]; $n -gt 0
""", ["0|int=True", "0|int=True", "1|int=True", "-1|int=True", "2|int=True", "-2|int=True",
            "2|long=True", "-2|long=True", "2.5|decimal=True", "-2.5|decimal=True", "2|double=True", "-2|double=True",
            "2|int=True", "-2.5|double=True", "0|int=True", "True", "2147483648|double=True", "True", "True"]);

        await VerifyAsync("invalid operands reject conversion", """
try { +@() } catch { 'plus-empty-array' }
try { +@(1) } catch { 'plus-single-array' }
try { -@(1, 2) } catch { 'minus-array' }
try { +'invalid' } catch { 'plus-string' }
try { @(1) - 1 } catch { 'subtract-array' }
try { 1 / @(1) } catch { 'divide-array' }
try { 1 % 'invalid' } catch { 'remainder-string' }
""", ["plus-empty-array", "plus-single-array", "minus-array", "plus-string", "subtract-array", "divide-array", "remainder-string"]);

        await VerifyAsync("prefix postfix and numeric increment types", """
$x = $null; $r = $x++; "$r|int=$($r -is [int])|stored=$x"
$x = $null; $r = --$x; "$r|int=$($r -is [int])|stored=$x"
$x = [byte]2; $r = $x++; "$r|byte=$($r -is [byte])|stored=$x|int=$($x -is [int])"
$x = [long]2; $r = ++$x; "$r|long=$($r -is [long])|storedLong=$($x -is [long])"
$x = [decimal]'2.5'; $r = $x--; "$r|decimal=$($r -is [decimal])|stored=$x|storedDecimal=$($x -is [decimal])"
$x = [double]2; $r = --$x; "$r|double=$($r -is [double])|storedDouble=$($x -is [double])"
$x = 2147483647; $r = ++$x; "$r|double=$($r -is [double])|storedDouble=$($x -is [double])"
$x = 0 + '-2147483648'; --$x; $x -is [double]
$x = 0 + '9223372036854775807'; ++$x; $x -is [double]
""", ["0|int=True|stored=1", "-1|int=True|stored=-1", "2|byte=True|stored=3|int=True",
            "3|long=True|storedLong=True", "2.5|decimal=True|stored=1.5|storedDecimal=True", "1|double=True|storedDouble=True",
            "2147483648|double=True|storedDouble=True", "True", "True"]);

        await VerifyAsync("numeric mutation and failed assignments", """
$items = @([long]5)
$r = $items[0]++; "$r|long=$($r -is [long])|stored=$($items[0])|storedLong=$($items[0] -is [long])"
$state = @{Amount=[decimal]'2.5'}
$r = --$state.Amount; "$r|decimal=$($r -is [decimal])|stored=$($state.Amount)"
$x = 9; try { $x /= 0 } catch { 'division-failed' }; $x
$x = [long]9; try { $x %= 0 } catch { 'remainder-failed' }; "$x|long=$($x -is [long])"
$x = '2'; try { $x++ } catch { 'string-increment-failed' }; "$x|string=$($x -is [string])"
$x = $true; try { --$x } catch { 'bool-decrement-failed' }; $x
$x = @(1); try { $x++ } catch { 'array-increment-failed' }; $x.Count; $x[0]
""", ["5|long=True|stored=6|storedLong=True", "1.5|decimal=True|stored=1.5", "division-failed", "9",
            "remainder-failed", "9|long=True", "string-increment-failed", "2|string=True", "bool-decrement-failed", "True",
            "array-increment-failed", "1", "1"]);

        await VerifyAsync("increment statement and expression output contexts", """
$x = 1
++ $x
"statement=$x"
(++$x)
$captured = $(++$x)
"subexpression-null=$($null -eq $captured)|stored=$x"
$captured = @(++$x)
"array-count=$($captured.Count)|value=$($captured[0])|stored=$x"
$captured = $((++$x))
"parenthesized-subexpression=$captured|stored=$x"
$captured = @((++$x))
"parenthesized-array-count=$($captured.Count)|value=$($captured[0])|stored=$x"
++ $x | ForEach-Object { "pipeline=$_" }
-- $x | ForEach-Object { "decrement-pipeline=$_" }
"after-pipeline=$x"
""", ["statement=2", "3", "subexpression-null=True|stored=4", "array-count=1|value=5|stored=5",
            "parenthesized-subexpression=6|stored=6", "parenthesized-array-count=1|value=7|stored=7",
            "pipeline=8", "decrement-pipeline=7", "after-pipeline=7"]);
    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Numeric operators ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}
