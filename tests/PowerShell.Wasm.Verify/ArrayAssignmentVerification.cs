using PSWasm;

internal static class ArrayAssignmentVerification
{
    public static async ValueTask VerifyAsync()
    {
        await VerifyAsync("heterogeneous arrays", """
$items = @('old', 7, $null)
$items[0] = 42
$items[1] = 'new'
$items[0] -is [int]
$items[1] -is [string]
$items -join ','
$items[-1] = @('nested', 9)
$items[2].Count
$items[2][0]
$items[0] = $null
$null -eq $items[0]
$items.Count
$items[-1] = $true
$items[2] -is [bool]
""", ["True", "True", "42,new,", "2", "nested", "True", "3", "True"]);

        await VerifyAsync("typed integer arrays", """
$items = [int[]]@(1, 2)
$items[0] = '3'
$items[-1] = $null
$items -join ','
$items[0] -is [int]
try { $items[0] = 'bad' } catch { 'int-conversion-error' }
$items[0]
try { $items[0] = 2147483648 } catch { 'int-overflow-error' }
$items[0]
try { $items[2] = 4 } catch { 'positive-index-error' }
try { $items[-3] = 4 } catch { 'negative-index-error' }
$items -join ','
""", ["3,0", "True", "int-conversion-error", "3", "int-overflow-error", "3",
            "positive-index-error", "negative-index-error", "3,0"]);

        await VerifyAsync("typed byte arrays", """
$items = [byte[]]@(1, 2)
$items[0] = '255'
$items[-1] = $null
$items -join ','
$items[0] -is [byte]
try { $items[0] = 256 } catch { 'byte-overflow-error' }
try { $items[0] = -1 } catch { 'byte-negative-error' }
try { $items[0] = 'bad' } catch { 'byte-conversion-error' }
$items -join ','
""", ["255,0", "True", "byte-overflow-error", "byte-negative-error", "byte-conversion-error", "255,0"]);

        await VerifyAsync("typed string and boolean arrays", """
$strings = [string[]]@('old', 'last')
$strings[0] = 42
$strings[-1] = $null
$strings[0] -is [string]
$null -eq $strings[1]
$strings[1].Length
$strings -join ','
$booleans = [bool[]]@($false, $true)
$booleans[0] = 'false'
$booleans[-1] = $null
$booleans -join ','
""", ["True", "False", "0", "42,", "True,False"]);

        await VerifyAsync("allowlisted mutable lists", """
$objects = [System.Collections.Generic.List[object]]@('old', 7)
$objects[0] = 42
$objects[-1] = 'new'
$objects[0] -is [int]
$objects[1] -is [string]
$objects -join ','
$numbers = [System.Collections.Generic.List[int]]@(1)
$numbers[0] = '3'
$numbers[0]
$numbers[0] = $null
$numbers[0]
try { $numbers[0] = 'bad' } catch { 'list-conversion-error' }
$numbers[0]
$mutable = [System.Collections.ArrayList]@('old', 7)
$mutable[0] = 42
$mutable[-1] = 'new'
$mutable[0] -is [int]
$mutable -join ','
""", ["True", "True", "42,new", "3", "0", "list-conversion-error", "0", "True", "42,new"]);
    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Array assignment ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}
