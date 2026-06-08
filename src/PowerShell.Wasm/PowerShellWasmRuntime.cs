using System.Text;
using System.Text.RegularExpressions;
using PSWasm.Commands;

namespace PSWasm;

public sealed class PowerShellWasmRuntime
{
    private readonly Dictionary<string, IPowerShellWasmCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly PowerShellWasmExecutionContext _executionContext;

    public PowerShellWasmRuntime(IDictionary<string, string>? environment = null)
    {
        _executionContext = new PowerShellWasmExecutionContext(environment);
        RegisterCommand("Write-Output", new WriteOutputCommand());
    }

    public void RegisterCommand(string name, IPowerShellWasmCommand command) =>
        _commands[name] = command;

    public async ValueTask<PowerShellWasmResult> ExecuteAsync(string script, CancellationToken cancellationToken = default)
    {
        foreach (var statement in SplitStatements(script))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryExecuteAssignment(statement))
            {
                continue;
            }

            await ExecuteCommandAsync(statement, cancellationToken);
        }

        return new PowerShellWasmResult([.. _executionContext.Output]);
    }

    private bool TryExecuteAssignment(string statement)
    {
        if (!statement.StartsWith('$'))
        {
            return false;
        }

        var equals = FindTopLevel(statement, '=');
        if (equals < 0)
        {
            return false;
        }

        var name = statement[1..equals].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        _executionContext.SetVariable(name, EvaluateExpression(statement[(equals + 1)..].Trim()));
        return true;
    }

    private async ValueTask ExecuteCommandAsync(string statement, CancellationToken cancellationToken)
    {
        var tokens = Tokenize(statement);
        if (tokens.Count == 0)
        {
            return;
        }

        var commandName = tokens[0];
        if (!_commands.TryGetValue(commandName, out var command))
        {
            throw new NotSupportedException($"Command '{commandName}' is not registered in this PSWasm runtime.");
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<object?>();

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (TryExpandSplat(token, parameters))
            {
                continue;
            }

            if (token.StartsWith('-') && token.Length > 1)
            {
                var name = token[1..];
                if (i + 1 >= tokens.Count || tokens[i + 1].StartsWith('-'))
                {
                    parameters[name] = true;
                    continue;
                }

                parameters[name] = EvaluateExpression(tokens[++i]);
                continue;
            }

            arguments.Add(EvaluateExpression(token));
        }

        var context = new PowerShellWasmCommandContext(_executionContext, parameters, arguments);
        await command.InvokeAsync(context, cancellationToken);
    }

    private bool TryExpandSplat(string token, Dictionary<string, object?> parameters)
    {
        if (!token.StartsWith('@') || token.StartsWith("@{") || token.Length == 1)
        {
            return false;
        }

        var variable = _executionContext.GetVariable(token[1..]);
        if (variable is not Dictionary<string, object?> hashtable)
        {
            throw new InvalidOperationException($"Splat variable '{token}' was not a hashtable.");
        }

        foreach (var item in hashtable)
        {
            parameters[item.Key] = item.Value;
        }

        return true;
    }

    private object? EvaluateExpression(string expression)
    {
        expression = expression.Trim();

        if (expression.StartsWith("@{") && expression.EndsWith('}'))
        {
            return ParseHashtable(expression[2..^1]);
        }

        if (expression.Length >= 2 && expression[0] == '\'' && expression[^1] == '\'')
        {
            return expression[1..^1];
        }

        if (expression.Length >= 2 && expression[0] == '"' && expression[^1] == '"')
        {
            return ExpandString(expression[1..^1]);
        }

        if (expression.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
        {
            return _executionContext.GetEnvironmentVariable(expression[5..]) ?? string.Empty;
        }

        if (expression.StartsWith('$'))
        {
            return _executionContext.GetVariable(expression[1..]);
        }

        return int.TryParse(expression, out var number) ? number : expression;
    }

    private Dictionary<string, object?> ParseHashtable(string body)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SplitTopLevel(body, ';'))
        {
            var equals = FindTopLevel(item, '=');
            if (equals < 0)
            {
                continue;
            }

            var key = item[..equals].Trim().Trim('\'', '"');
            var value = item[(equals + 1)..].Trim();
            result[key] = EvaluateExpression(value);
        }

        return result;
    }

    private string ExpandString(string value)
    {
        return Regex.Replace(value, @"\$(env:)?([A-Za-z_][A-Za-z0-9_]*)", match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Success)
            {
                return _executionContext.GetEnvironmentVariable(name) ?? string.Empty;
            }

            return _executionContext.GetVariable(name)?.ToString() ?? string.Empty;
        });
    }

    private static IEnumerable<string> SplitStatements(string script)
    {
        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            var statement = StripComment(line).Trim();
            if (statement.Length > 0)
            {
                yield return statement;
            }
        }
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote == '\0' && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                continue;
            }

            if (quote != '\0' && ch == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote == '\0' && ch == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static List<string> Tokenize(string statement)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var ch in statement)
        {
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }

            if (quote == '\0' && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (quote != '\0' && ch == quote)
            {
                quote = '\0';
                current.Append(ch);
                continue;
            }

            current.Append(ch);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var start = 0;
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote == '\0' && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                continue;
            }

            if (quote != '\0' && ch == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote != '\0')
            {
                continue;
            }

            depth += ch switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0 && ch == separator)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..].Trim();
        }
    }

    private static int FindTopLevel(string text, char target)
    {
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote == '\0' && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                continue;
            }

            if (quote != '\0' && ch == quote)
            {
                quote = '\0';
                continue;
            }

            if (quote != '\0')
            {
                continue;
            }

            depth += ch switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0 && ch == target)
            {
                return i;
            }
        }

        return -1;
    }
}
