using PSWasm;

internal static class AssignmentEvaluationVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Expected output is cross-checked with native PowerShell, including evaluation order and failed writes.
        await VerifyAsync("index prefix and postfix evaluate keys once", """
$i=0; $a=@(10,20); $r=$a[$i++]++; "$r|$i|$($a -join ',')"
$i=0; $a=@(10,20); $r=++$a[$i++]; "$r|$i|$($a -join ',')"
$i=0; $a=@(10,20); $r=$a[$i++]--; "$r|$i|$($a -join ',')"
$i=0; $a=@(10,20); $r=--$a[$i++]; "$r|$i|$($a -join ',')"
""", ["10|1|11,20", "11|1|11,20", "10|1|9,20", "9|1|9,20"]);

        await VerifyAsync("nested index receiver and key evaluate once", """
$r=0; $k=0; $keys=@('A','B'); $boxes=@(@{A=10;B=20},@{A=30;B=40})
$value=$boxes[$r++][$keys[$k++]]++
"$value|$r|$k|$($boxes[0].A)|$($boxes[0].B)|$($boxes[1].A)|$($boxes[1].B)"
$r=0; $k=0; $boxes[$r++][$keys[$k++]] += 2
"$r|$k|$($boxes[0].A)|$($boxes[1].A)"
""", ["10|1|1|11|20|30|40", "1|1|13|30"]);

        await VerifyAsync("computed member prefix and postfix capture their names", """
$i=0; $keys=@('A','B'); $state=@{A=10;B=20}; $r=$state.($keys[$i++])++
"$r|$i|$($state.A)|$($state.B)"
$i=0; $r=--$state.($keys[$i++]); "$r|$i|$($state.A)|$($state.B)"
""", ["10|1|11|20", "10|1|10|20"]);

        await VerifyAsync("all compound numeric index operators evaluate key once", """
$i=0; $a=@(10,20); $a[$i++] += 2; "$i|$($a -join ',')"
$i=0; $a=@(10,20); $a[$i++] -= 2; "$i|$($a -join ',')"
$i=0; $a=@(10,20); $a[$i++] *= 2; "$i|$($a -join ',')"
$i=0; $a=@(10,20); $a[$i++] /= 2; "$i|$($a -join ',')"
$i=0; $a=@(10,20); $a[$i++] %= 3; "$i|$($a -join ',')"
""", ["1|12,20", "1|8,20", "1|20,20", "1|5,20", "1|1,20"]);

        await VerifyAsync("ordinary index assignment evaluates value before receiver and key", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver)[(Get-Key)] = (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["VRK|2"]);

        await VerifyAsync("compound index assignment evaluates receiver key then value", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver)[(Get-Key)] += (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["RKV|12"]);

        await VerifyAsync("ordinary computed member assignment evaluates receiver key then value", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).(Get-Key) = (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["RKV|2"]);

        await VerifyAsync("ordinary named member assignment evaluates receiver then value", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).Value = (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["RV|2"]);

        await VerifyAsync("compound computed member assignment evaluates receiver key then value", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).(Get-Key) *= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["RKV|20"]);

        await VerifyAsync("named member increment captures receiver once", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
$r=(Get-Receiver).Value++; "returned=$r"
"$($state.Log)|$($state.Target.Value)"
""", ["returned=10", "R|11"]);

        await VerifyAsync("index statement assignment evaluates statement before target", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver)[(Get-Key)] = if ($true) { Get-Value }
"$($state.Log)|$($state.Target.Value)"
""", ["VRK|2"]);

        await VerifyAsync("member statement assignment evaluates target before statement", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).Value = if ($true) { Get-Value }
"$($state.Log)|$($state.Target.Value)"
""", ["RV|2"]);

        await VerifyAsync("computed member statement assignment evaluates target before statement", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).(Get-Key) = if ($true) { Get-Value }
"$($state.Log)|$($state.Target.Value)"
""", ["RKV|2"]);

        await VerifyAsync("ordinary index assignment resolves replacement receiver and key after value", """
$old=@(10,20); $state=@{Items=$old}; $i=0
$state.Items[$i] = $($state.Items=@(30,40); $i=1; 99)
"$i|old=$($old -join ',')|new=$($state.Items -join ',')"
""", ["1|old=10,20|new=30,99"]);

        await VerifyAsync("compound index assignment preserves receiver key and value before RHS changes", """
$old=@(10,20); $state=@{Items=$old}; $i=0
$state.Items[$i] += $($old[0]=50; $state.Items=@(30,40); $i=1; 2)
"$i|old=$($old -join ',')|new=$($state.Items -join ',')"
""", ["1|old=12,20|new=30,40"]);

        await VerifyAsync("ordinary member assignment preserves receiver and name across RHS changes", """
$old=@{A=10;B=20}; $state=@{Target=$old;Key='A'}
$state.Target.($state.Key) = $($state.Target=@{A=30;B=40}; $state.Key='B'; 99)
"$($state.Key)|old=$($old.A),$($old.B)|new=$($state.Target.A),$($state.Target.B)"
$old=@{A=10}; $state.Target=$old
$state.Target.A = $($state.Target=@{A=30}; 99)
"old=$($old.A)|new=$($state.Target.A)"
""", ["B|old=99,20|new=30,40", "old=99|new=30"]);

        await VerifyAsync("compound member assignment preserves receiver name and read value", """
$old=@{A=10;B=20}; $state=@{Target=$old;Key='A'}
$state.Target.($state.Key) += $($old.A=50; $state.Target=@{A=30;B=40}; $state.Key='B'; 2)
"$($state.Key)|old=$($old.A),$($old.B)|new=$($state.Target.A),$($state.Target.B)"
""", ["B|old=12,20|new=30,40"]);

        await VerifyAsync("null coalescing index assignment short circuits and captures null location", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver)[(Get-Key)] ??= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
$state.Log=''; $state.Target.Value=$null
(Get-Receiver)[(Get-Key)] ??= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
""", ["RK|10", "RKV|2"]);

        await VerifyAsync("null coalescing member assignment handles missing properties and false values", """
$state=@{Log='';Target=@{}}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
(Get-Receiver).(Get-Key) ??= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
$state.Log=''; $state.Target.Value=$false
(Get-Receiver).(Get-Key) ??= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
$state.Log=''; $state.Target.Value=0
(Get-Receiver).Value ??= (Get-Value)
"$($state.Log)|$($state.Target.Value)"
$state.Log=''; $state.Target.Value=''
(Get-Receiver).Value ??= (Get-Value)
"$($state.Log)|empty=$($state.Target.Value -eq '')"
""", ["RKV|2", "RK|False", "R|0", "R|empty=True"]);

        await VerifyAsync("null coalescing assignment preserves index when RHS replaces receiver", """
$old=@($null,20); $state=@{Items=$old}; $i=0
$state.Items[$i] ??= $($state.Items=@(30,40); $i=1; 99)
"$i|old=$($old -join ',')|new=$($state.Items -join ',')"
""", ["1|old=99,20|new=30,40"]);

        await VerifyAsync("variable compound assignment reads once before RHS", """
$x=10; $x += $($x=50; 2); $x
$x=$null; $x ??= $($x=50; 2); $x
$x=0; $x ??= $($x=50; 2); $x
""", ["12", "2", "0"]);

        await VerifyAsync("failed increment and arithmetic preserve collection values after target side effects", """
$i=0; $a=@('x',20)
try { $a[$i++]++ } catch { 'increment-failed' }
"$i|$($a -join ',')"
$i=0; $a=@(10,20)
try { $a[$i++] /= 0 } catch { 'division-failed' }
"$i|$($a -join ',')"
""", ["increment-failed", "1|x,20", "division-failed", "1|10,20"]);

        await VerifyAsync("compound target errors prevent RHS evaluation", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; throw 'receiver' }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
try { (Get-Receiver)[(Get-Key)] += (Get-Value) } catch { 'receiver-failed' }
$state.Log
$state.Log=''
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; throw 'key' }
try { (Get-Receiver)[(Get-Key)] += (Get-Value) } catch { 'key-failed' }
$state.Log
""", ["receiver-failed", "R", "key-failed", "RK"]);

        await VerifyAsync("ordinary assignment target and RHS failures respect differing evaluation order", """
$state=@{Log='';Target=@{Value=10}}
function Get-Receiver { $state.Log += 'R'; throw 'receiver' }
function Get-Key { $state.Log += 'K'; 'Value' }
function Get-Value { $state.Log += 'V'; 2 }
try { (Get-Receiver)[(Get-Key)] = (Get-Value) } catch { 'index-receiver-failed' }
$state.Log
$state.Log=''
try { (Get-Receiver).(Get-Key) = (Get-Value) } catch { 'member-receiver-failed' }
$state.Log
$state.Log=''
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Value { $state.Log += 'V'; throw 'value' }
try { (Get-Receiver)[(Get-Key)] = (Get-Value) } catch { 'index-value-failed' }
$state.Log
$state.Log=''
try { (Get-Receiver).(Get-Key) = (Get-Value) } catch { 'member-value-failed' }
"$($state.Log)|$($state.Target.Value)"
""", ["index-receiver-failed", "VR", "member-receiver-failed", "R", "index-value-failed", "V", "member-value-failed", "RKV|10"]);

        await VerifyAsync("typed array increment returns calculated prefix and original postfix value", """
$i=0; $a=[byte[]]@(2,4); $r=++$a[$i++]
"$r|byte=$($r -is [byte])|int=$($r -is [int])|$i|$($a -join ',')"
$i=0; $a=[byte[]]@(2,4); $r=$a[$i++]++
"$r|byte=$($r -is [byte])|int=$($r -is [int])|$i|$($a -join ',')"
$i=0; $a=[byte[]]@(255,4)
try { ++$a[$i++] } catch { 'byte-overflow' }
"$i|$($a -join ',')"
""", ["3|byte=False|int=True|1|3,4", "2|byte=True|int=False|1|3,4", "byte-overflow", "1|255,4"]);

        await VerifyAsync("null coalescing assignment still writes existing values to readonly targets", """
$state=@{Log='';Target='abc'}
function Get-Receiver { $state.Log += 'R'; $state.Target }
function Get-Key { $state.Log += 'K'; 0 }
function Get-Value { $state.Log += 'V'; 'z' }
try { (Get-Receiver)[(Get-Key)] ??= (Get-Value) } catch { 'readonly-index' }
"$($state.Log)|$($state.Target)"
$a=@(1,2)
try { $a.Count ??= 3 } catch { 'readonly-member' }
$a.Count
""", ["readonly-index", "RK|abc", "readonly-member", "2"]);

        await VerifyAsync("typed assignment expressions preserve value before storage conversion", """
$a=[int[]]@(1,2); $i=0; $r=($a[$i++]='3')
"$r|string=$($r -is [string])|$i|stored=$($a[0])|int=$($a[0] -is [int])"
$a=[int[]]@(9,2); $i=0; $r=($a[$i++] /= 2)
"$r|double=$($r -is [double])|$i|stored=$($a[0])|int=$($a[0] -is [int])"
$list=[System.Collections.Generic.List[int]]@(1,2); $i=0; $r=($list[$i++]='3')
"$r|string=$($r -is [string])|$i|stored=$($list[0])|int=$($list[0] -is [int])"
""", ["3|string=True|1|stored=3|int=True", "4.5|double=True|1|stored=4|int=True", "3|string=True|1|stored=3|int=True"]);

        await VerifyAsync("negative list indices evaluate once across mutation and resizing", """
$i=-1; $numbers=[System.Collections.Generic.List[int]]@(10,20); $r=$numbers[$i--]++
"$r|$i|$($numbers -join ',')"
$i=-1; $numbers[$i--] += 2
"$i|$($numbers -join ',')"
$i=-1; $mutable=[System.Collections.ArrayList]@(10,20); $r=--$mutable[$i--]
"$r|$i|$($mutable -join ',')"
$i=-1; $numbers=[System.Collections.Generic.List[int]]@(10,20)
$numbers[$i--] += $($numbers.Add(30); 2)
"$i|$($numbers -join ',')"
""", ["20|-2|10,21", "-2|10,23", "19|-2|10,19", "-2|10,20,22"]);

        await VerifyAsync("assignment statements suppress output while grouped assignments return values", """
$state=@{Calls=0;Target=@{Value=1}}
function Get-Target { $state.Calls++; $state.Target }
$r=@((Get-Target).Value = 2)
"$($r.Count)|$($state.Calls)|$($state.Target.Value)"
$r=@(((Get-Target).Value = 3))
"$($r.Count)|$($r[0])|$($state.Calls)|$($state.Target.Value)"
$r=@((Get-Target).Value += 2)
"$($r.Count)|$($state.Calls)|$($state.Target.Value)"
$r=@(((Get-Target).Value += 2))
"$($r.Count)|$($r[0])|$($state.Calls)|$($state.Target.Value)"
""", ["0|1|2", "1|3|2|3", "0|3|5", "1|7|4|7"]);

        await VerifyAsync("equals command arguments after pipeline and chain are not assignments", """
function F { param($x) $x };
1 | F =;
1 && Write-Output =;
1 || Write-Output =;
(1 | F =);
$result=(1 && Write-Output =); $result.Count; $result -join ',';
""", ["=", "1", "=", "1", "=", "2", "1,="]);

    }

    private static async ValueTask VerifyAsync(string scenario, string script, string[] expected)
    {
        var result = await new PowerShellWasmRuntime().ExecuteAsync(script);
        if (!result.Output.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Assignment evaluation ({scenario}) expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
                $"{Environment.NewLine}Actual:{Environment.NewLine}{result.Text}");
        }
    }
}

