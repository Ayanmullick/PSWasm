using PSWasm;

internal static class FunctionScopeVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Native PowerShell 7.6.1 supplied these expectations in independent top-level sessions.
        // Behavioral guidance: scoped function declaration and private ancestor lookup.
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/SessionStateFunctionAPIs.cs#L498-L548
        // https://github.com/PowerShell/PowerShell/blob/411d5fee10110d9881a909804f9d4eb1a06052ea/src/System.Management.Automation/engine/ScopedItemSearcher.cs#L383-L418
        // Get-Command intentionally lists private ancestors even when invocation cannot access them.
        var cases = new (string Name, string Script, string[] Expected)[]
        {
            ("global_from_child",
                "& { function global:ScopeFoo { 'global' } }; ScopeFoo; & { ScopeFoo }",
                ["global", "global"]),
            ("script_from_child",
                "& { function script:ScopeFoo { 'script' } }; ScopeFoo; & { ScopeFoo }",
                ["script", "script"]),
            ("local_child_lifetime",
                "& { function local:ScopeFoo { 'local' }; ScopeFoo; & { ScopeFoo } }; try { ScopeFoo } catch { 'missing' }",
                ["local", "local", "missing"]),
            ("ordinary_child_lifetime",
                "& { function ScopeFoo { 'child' }; ScopeFoo; & { ScopeFoo } }; try { ScopeFoo } catch { 'missing' }",
                ["child", "child", "missing"]),
            ("private_root_visibility",
                "function private:ScopeFoo { 'private' }; ScopeFoo; & { try { ScopeFoo } catch { 'hidden' } }; . { ScopeFoo }",
                ["private", "hidden", "private"]),
            ("private_child_fallback",
                "function ScopeFoo { 'outer' }; & { function private:ScopeFoo { 'private' }; ScopeFoo; & { ScopeFoo }; . { ScopeFoo } }; ScopeFoo",
                ["private", "outer", "private", "outer"]),
            ("private_child_no_fallback",
                "& { function private:ScopeFoo { 'private' }; ScopeFoo; & { try { ScopeFoo } catch { 'hidden' } } }; try { ScopeFoo } catch { 'missing' }",
                ["private", "hidden", "missing"]),
            ("private_explicit_scope_hidden",
                "function private:ScopeFoo {'private'}; & { try {global:ScopeFoo} catch {'global-hidden'}; try {script:ScopeFoo} catch {'script-hidden'}; try {local:ScopeFoo} catch {'local-hidden'}; try {private:ScopeFoo} catch {'private-hidden'} }",
                ["global-hidden", "script-hidden", "local-hidden", "private-hidden"]),
            ("scoped_direct_lookup",
                "function ScopeFoo { 'root' }; & { function ScopeFoo { 'child' }; ScopeFoo; global:ScopeFoo; script:ScopeFoo; local:ScopeFoo; private:ScopeFoo; & { global:ScopeFoo; script:ScopeFoo; try { local:ScopeFoo } catch {'missing-local'}; try { private:ScopeFoo } catch {'missing-private'} } }",
                ["child", "root", "root", "child", "child", "root", "root", "missing-local", "missing-private"]),
            ("scoped_call_operator",
                "function ScopeFoo {'root'}; & { function ScopeFoo {'child'}; & 'global:ScopeFoo'; & 'script:ScopeFoo'; & 'local:ScopeFoo'; & 'private:ScopeFoo' }",
                ["root", "root", "child", "child"]),
            ("scoped_dot_call",
                "function ScopeFoo {'root'}; & { function ScopeFoo {'child'}; . 'global:ScopeFoo'; . 'script:ScopeFoo'; . 'local:ScopeFoo'; . 'private:ScopeFoo' }",
                ["root", "root", "child", "child"]),
            ("dot_call_scope_mutation",
                "function ScopeFoo { $scopeValue='function' }; $scopeValue='root'; ScopeFoo; $scopeValue; & 'ScopeFoo'; $scopeValue; . 'ScopeFoo'; $scopeValue",
                ["root", "root", "function"]),
            ("private_call_operator",
                "function private:ScopeFoo {'private'}; & 'ScopeFoo'; . 'ScopeFoo'; & { try { & 'ScopeFoo' } catch {'hidden-call'}; try { . 'ScopeFoo' } catch {'hidden-dot'} }",
                ["private", "private", "hidden-call", "hidden-dot"]),
            ("private_recursion",
                "function private:ScopeFoo {param($n) if ($n -eq 0){'done'} else {try{ScopeFoo ($n - 1)}catch{'hidden-self'}}}; ScopeFoo 1; . ScopeFoo 1",
                ["hidden-self", "done"]),
            ("qualifier_case",
                "& { function GlObAl:ScopeFoo {'global'}; function ScRiPt:ScopeBar {'script'}; function LoCaL:ScopeBaz {'local'}; function PrIvAtE:ScopeQux {'private'}; sCoPeFoO; SCRIPT:scopebar; LOCAL:scopebaz; PRIVATE:scopequx; & {SCOPEFOO; scopebar; ScopeBaz; try{ScopeQux}catch{'hidden'}} }; ScopeFoo; ScopeBar",
                ["global", "script", "local", "private", "global", "script", "local", "hidden", "global", "script"]),
            ("shadow_and_restore",
                "function ScopeFoo {'root'}; & {function ScopeFoo {'child'}; ScopeFoo; & {function ScopeFoo {'grandchild'}; ScopeFoo}; ScopeFoo}; ScopeFoo",
                ["child", "grandchild", "child", "root"]),
            ("local_redefine_preserves_outer",
                "function ScopeFoo {'root'}; & {function local:ScopeFoo {'child'}; function ScopeFoo {'replacement'}; ScopeFoo}; ScopeFoo",
                ["replacement", "root"]),
            ("global_redefine_from_child",
                "function ScopeFoo {'old'}; & {function global:ScopeFoo {'new'}; ScopeFoo}; ScopeFoo",
                ["new", "new"]),
            ("script_redefine_from_function",
                "function ScopeFoo {'old'}; function ScopeDefine {function script:ScopeFoo {'new'}; ScopeFoo}; ScopeDefine; ScopeFoo",
                ["new", "new"]),
            ("private_to_ordinary_redefinition",
                "function private:ScopeFoo {'first'}; function ScopeFoo {'second'}; ScopeFoo; & { ScopeFoo }",
                ["second", "second"]),
            ("private_to_local_redefinition",
                "function private:ScopeFoo {'first'}; function local:ScopeFoo {'second'}; ScopeFoo; & { ScopeFoo }",
                ["second", "second"]),
            ("public_to_private_redefinition",
                "function ScopeFoo {'first'}; function private:ScopeFoo {'second'}; ScopeFoo; & {try {ScopeFoo} catch {'hidden'}}",
                ["second", "hidden"]),
            ("private_global_redefinition_from_child",
                "function private:ScopeFoo {'old'}; & {function global:ScopeFoo {'new'}; ScopeFoo}; ScopeFoo; & {ScopeFoo}",
                ["new", "new", "new"]),
            ("case_insensitive_redefinition",
                "function ScopeFoo {'first'}; function scopefoo {'second'}; SCOPEFOO; & {sCoPeFoO}",
                ["second", "second"]),
            ("nested_function_lifetime",
                "function ScopeDefine {function ScopeFoo {'nested'}; ScopeFoo}; ScopeDefine; try {ScopeFoo} catch {'missing'}; . ScopeDefine; ScopeFoo",
                ["nested", "missing", "nested", "nested"]),
            ("nested_global_function",
                "function ScopeDefine {function global:ScopeFoo {'nested-global'}}; ScopeDefine; ScopeFoo; & {ScopeFoo}",
                ["nested-global", "nested-global"]),
            ("nested_local_function",
                "function ScopeDefine {function local:ScopeFoo {'nested-local'}; ScopeFoo; & {ScopeFoo}}; ScopeDefine; try {ScopeFoo} catch {'missing'}",
                ["nested-local", "nested-local", "missing"]),
            ("nested_private_function",
                "function ScopeDefine {function private:ScopeFoo {'nested-private'}; ScopeFoo; & {try {ScopeFoo} catch {'hidden'}}; . {ScopeFoo}}; ScopeDefine; try {ScopeFoo} catch {'missing'}",
                ["nested-private", "hidden", "nested-private", "missing"]),
            ("declaration_scope_not_closure",
                "& {$local:scopeValue='child'; function global:ScopeFoo {$scopeValue}; ScopeFoo}; $scopeValue='root'; ScopeFoo; & {$local:scopeValue='caller'; global:ScopeFoo}",
                ["child", "root", "caller"]),
            ("scoped_builtin_rejected",
                "try { global:Write-Output 'value' } catch {'missing'}; try { & 'local:Write-Output' 'value' } catch {'missing-local'}",
                ["missing", "missing-local"]),
            ("unknown_qualifier_literal",
                "function strange:ScopeFoo {'literal'}; strange:ScopeFoo; & 'STRANGE:SCOPEFOO'; . 'strange:ScopeFoo'; try {ScopeFoo} catch {'missing'}",
                ["literal", "literal", "literal", "missing"]),
            ("unknown_empty_suffix_literal",
                "function unknown: {'literal'}; unknown:; & 'unknown:'",
                ["literal", "literal"]),
            ("nested_recognized_qualifier_literal",
                "function global:local:ScopeFoo {'nested'}; & 'global:local:ScopeFoo'; . 'global:local:ScopeFoo'; try {& 'local:ScopeFoo'} catch {'missing-local'}; try {& 'ScopeFoo'} catch {'missing-plain'}",
                ["nested", "nested", "missing-local", "missing-plain"]),
            ("known_then_unknown_qualifier",
                "& {function global:unknown:ScopeFoo {'nested'}}; unknown:ScopeFoo; global:unknown:ScopeFoo",
                ["nested", "nested"]),
            ("unknown_then_known_literal",
                "function unknown:global:ScopeFoo {'literal'}; unknown:global:ScopeFoo; & 'unknown:global:ScopeFoo'; try{ScopeFoo}catch{'missing'}",
                ["literal", "literal", "missing"]),
            ("empty_known_qualifier_rejected",
                "try {function global: {'bad'}; 'not-run'} catch {'global-rejected'}; try {function script: {'bad'}; 'not-run'} catch {'script-rejected'}; try {function local: {'bad'}; 'not-run'} catch {'local-rejected'}; try {function private: {'bad'}; 'not-run'} catch {'private-rejected'}; 'after'",
                ["global-rejected", "script-rejected", "local-rejected", "private-rejected", "after"]),
            ("get_command_private_ancestor",
                "function private:ScopeFoo {'private'}; (Get-Command ScopeFoo).Name; & {(Get-Command ScopeFoo).Name; @(Get-Command '*ScopeFoo*').Count; try{ScopeFoo}catch{'hidden'}}",
                ["ScopeFoo", "ScopeFoo", "1", "hidden"]),
            ("get_command_shadow_unique",
                "function ScopeFoo {'root'}; & {function private:ScopeFoo {'private'}; @(Get-Command '*ScopeFoo*').Count; & {@(Get-Command '*ScopeFoo*').Count; (Get-Command ScopeFoo).Name; ScopeFoo}}",
                ["1", "1", "ScopeFoo", "root"]),
            ("get_command_local_lifetime",
                "& {function local:ScopeFoo {'local'}; (Get-Command ScopeFoo).Name}; @(Get-Command '*ScopeFoo*').Count",
                ["ScopeFoo", "0"]),
            ("get_command_colon_names",
                "function global:local:ScopeFoo {'nested'}; function unknown:ScopeBar {'literal'}; (Get-Command '*ScopeFoo*').Name; (Get-Command '*ScopeBar*').Name",
                ["local:ScopeFoo", "unknown:ScopeBar"]),
            ("scoped_parameter_alias",
                "& {function global:ScopeFoo {param([Alias('n')]$Name) $Name}}; ScopeFoo -N 'direct'; & 'GLOBAL:scopefoo' -n 'call'; . 'global:ScopeFoo' -N 'dot'",
                ["direct", "call", "dot"]),
            ("private_parameter_alias",
                "function private:ScopeFoo {param([Alias('n')]$Name) $Name}; ScopeFoo -N 'direct'; & 'ScopeFoo' -n 'call'; . 'PRIVATE:ScopeFoo' -N 'dot'",
                ["direct", "call", "dot"]),
            ("private_foreach_callback_scope",
                "1 | ForEach-Object {function private:ScopeFoo {'callback'}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-inner'}}}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-outer'}}",
                ["callback", "hidden-inner", "callback", "hidden-outer"]),
            ("private_catch_scope",
                "try {throw 'sample'} catch {function private:ScopeFoo {'catch'}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-inner'}}}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-outer'}}",
                ["catch", "hidden-inner", "catch", "hidden-outer"]),
            ("private_switch_action_scope",
                "switch (1) {1 {function private:ScopeFoo {'switch'}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-inner'}}}}; ScopeFoo; & {try {ScopeFoo} catch {'hidden-outer'}}",
                ["switch", "hidden-inner", "switch", "hidden-outer"]),
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
        Console.WriteLine($"Function scope scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        await VerifySessionReuseAsync();
    }
    private static async ValueTask VerifySessionReuseAsync()
    {
        var runtime = new PowerShellWasmRuntime();
        var steps = new (string Name, string Script, string[] Expected)[]
        {
            ("session step 1", "& { function global:Get-ScopeSession { 'global-v1' }; function script:Get-ScopeScript { 'script' }; function local:Get-ScopeEphemeral { 'child' }; Get-ScopeEphemeral }; function private:Get-ScopeSecret { 'secret' }; function Get-ScopeParent { 'parent' }; 'defined'", ["child", "defined"]),
            ("session step 2", "Get-ScopeSession; Get-ScopeScript; Get-ScopeSecret; try { Get-ScopeEphemeral } catch { 'child-gone' }", ["global-v1", "script", "secret", "child-gone"]),
            ("session step 3", "& { function private:Get-ScopeParent { 'private-child' }; Get-ScopeParent; & { Get-ScopeParent }; function global:Get-ScopeSession { 'global-v2' }; try { Get-ScopeSecret } catch { 'private-hidden' } }; Get-ScopeParent", ["private-child", "parent", "private-hidden", "parent"]),
            ("session step 4", "Get-ScopeSession; Get-ScopeScript; Get-ScopeSecret; Get-ScopeParent", ["global-v2", "script", "secret", "parent"]),
        };
        foreach (var (name, script, expected) in steps)
        {
            var result = await runtime.ExecuteAsync(script);
            var records = expected.Select(static text => new PowerShellWasmOutputRecord("Output", text));
            if (!result.Records.SequenceEqual(records))
                throw new InvalidOperationException($"{name}: expected {string.Join(" | ", expected)}; actual {result.Text}");
        }
        Console.WriteLine("Function scope session steps: 4/4 passed.");
    }
}
