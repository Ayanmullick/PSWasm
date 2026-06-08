namespace PSWasm.Language;

// Adapted from PowerShell source: src/System.Management.Automation/engine/parser/token.cs
// Browser note: this is a flattened browser profile of TokenKind/TokenTraits. It keeps operator vocabulary
// and precedence without reproducing the full command/expression mode parser machinery from SMA.
internal static class PowerShellWasmTokenTraits
{
    private static readonly Dictionary<string, PowerShellWasmTokenKind> s_operatorText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["%"] = PowerShellWasmTokenKind.Remainder,
        [".."] = PowerShellWasmTokenKind.DotDot,
        ["??"] = PowerShellWasmTokenKind.QuestionQuestion,
        ["-f"] = PowerShellWasmTokenKind.Format,
        ["-not"] = PowerShellWasmTokenKind.Not,
        ["-bnot"] = PowerShellWasmTokenKind.Bnot,
        ["-and"] = PowerShellWasmTokenKind.And,
        ["-or"] = PowerShellWasmTokenKind.Or,
        ["-xor"] = PowerShellWasmTokenKind.Xor,
        ["-band"] = PowerShellWasmTokenKind.Band,
        ["-bor"] = PowerShellWasmTokenKind.Bor,
        ["-bxor"] = PowerShellWasmTokenKind.Bxor,
        ["-join"] = PowerShellWasmTokenKind.Join,
        ["-eq"] = PowerShellWasmTokenKind.Ieq,
        ["-ieq"] = PowerShellWasmTokenKind.Ieq,
        ["-ne"] = PowerShellWasmTokenKind.Ine,
        ["-ine"] = PowerShellWasmTokenKind.Ine,
        ["-ge"] = PowerShellWasmTokenKind.Ige,
        ["-ige"] = PowerShellWasmTokenKind.Ige,
        ["-gt"] = PowerShellWasmTokenKind.Igt,
        ["-igt"] = PowerShellWasmTokenKind.Igt,
        ["-lt"] = PowerShellWasmTokenKind.Ilt,
        ["-ilt"] = PowerShellWasmTokenKind.Ilt,
        ["-le"] = PowerShellWasmTokenKind.Ile,
        ["-ile"] = PowerShellWasmTokenKind.Ile,
        ["-like"] = PowerShellWasmTokenKind.Ilike,
        ["-ilike"] = PowerShellWasmTokenKind.Ilike,
        ["-notlike"] = PowerShellWasmTokenKind.Inotlike,
        ["-inotlike"] = PowerShellWasmTokenKind.Inotlike,
        ["-match"] = PowerShellWasmTokenKind.Imatch,
        ["-imatch"] = PowerShellWasmTokenKind.Imatch,
        ["-notmatch"] = PowerShellWasmTokenKind.Inotmatch,
        ["-inotmatch"] = PowerShellWasmTokenKind.Inotmatch,
        ["-replace"] = PowerShellWasmTokenKind.Ireplace,
        ["-ireplace"] = PowerShellWasmTokenKind.Ireplace,
        ["-contains"] = PowerShellWasmTokenKind.Icontains,
        ["-icontains"] = PowerShellWasmTokenKind.Icontains,
        ["-notcontains"] = PowerShellWasmTokenKind.Inotcontains,
        ["-inotcontains"] = PowerShellWasmTokenKind.Inotcontains,
        ["-in"] = PowerShellWasmTokenKind.Iin,
        ["-iin"] = PowerShellWasmTokenKind.Iin,
        ["-notin"] = PowerShellWasmTokenKind.Inotin,
        ["-inotin"] = PowerShellWasmTokenKind.Inotin,
        ["-split"] = PowerShellWasmTokenKind.Isplit,
        ["-isplit"] = PowerShellWasmTokenKind.Isplit,
        ["-ceq"] = PowerShellWasmTokenKind.Ceq,
        ["-cne"] = PowerShellWasmTokenKind.Cne,
        ["-cge"] = PowerShellWasmTokenKind.Cge,
        ["-cgt"] = PowerShellWasmTokenKind.Cgt,
        ["-clt"] = PowerShellWasmTokenKind.Clt,
        ["-cle"] = PowerShellWasmTokenKind.Cle,
        ["-clike"] = PowerShellWasmTokenKind.Clike,
        ["-cnotlike"] = PowerShellWasmTokenKind.Cnotlike,
        ["-cmatch"] = PowerShellWasmTokenKind.Cmatch,
        ["-cnotmatch"] = PowerShellWasmTokenKind.Cnotmatch,
        ["-creplace"] = PowerShellWasmTokenKind.Creplace,
        ["-ccontains"] = PowerShellWasmTokenKind.Ccontains,
        ["-cnotcontains"] = PowerShellWasmTokenKind.Cnotcontains,
        ["-cin"] = PowerShellWasmTokenKind.Cin,
        ["-cnotin"] = PowerShellWasmTokenKind.Cnotin,
        ["-csplit"] = PowerShellWasmTokenKind.Csplit,
        ["-shl"] = PowerShellWasmTokenKind.Shl,
        ["-shr"] = PowerShellWasmTokenKind.Shr
    };

    public static bool TryGetOperator(string text, out PowerShellWasmTokenKind kind) =>
        s_operatorText.TryGetValue(text, out kind);

    public static bool IsBinaryOperator(this PowerShellWasmTokenKind kind) =>
        GetBinaryPrecedence(kind) > 0;

    public static bool IsUnaryOperator(this PowerShellWasmTokenKind kind) =>
        kind is PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus or PowerShellWasmTokenKind.Not or
            PowerShellWasmTokenKind.Bnot or PowerShellWasmTokenKind.Join or PowerShellWasmTokenKind.Isplit or
            PowerShellWasmTokenKind.Csplit;

    public static int GetBinaryPrecedence(this PowerShellWasmTokenKind kind) =>
        kind switch
        {
            PowerShellWasmTokenKind.QuestionQuestion => 1,
            PowerShellWasmTokenKind.And or PowerShellWasmTokenKind.Or or PowerShellWasmTokenKind.Xor => 2,
            PowerShellWasmTokenKind.Band or PowerShellWasmTokenKind.Bor or PowerShellWasmTokenKind.Bxor => 3,
            PowerShellWasmTokenKind.DotDot => 4,
            PowerShellWasmTokenKind.Format => 5,
            PowerShellWasmTokenKind.Ieq or PowerShellWasmTokenKind.Ine or PowerShellWasmTokenKind.Ige or
                PowerShellWasmTokenKind.Igt or PowerShellWasmTokenKind.Ilt or PowerShellWasmTokenKind.Ile or
                PowerShellWasmTokenKind.Ilike or PowerShellWasmTokenKind.Inotlike or PowerShellWasmTokenKind.Imatch or
                PowerShellWasmTokenKind.Inotmatch or PowerShellWasmTokenKind.Ireplace or PowerShellWasmTokenKind.Icontains or
                PowerShellWasmTokenKind.Inotcontains or PowerShellWasmTokenKind.Iin or PowerShellWasmTokenKind.Inotin or
                PowerShellWasmTokenKind.Isplit or PowerShellWasmTokenKind.Ceq or PowerShellWasmTokenKind.Cne or
                PowerShellWasmTokenKind.Cge or PowerShellWasmTokenKind.Cgt or PowerShellWasmTokenKind.Clt or
                PowerShellWasmTokenKind.Cle or PowerShellWasmTokenKind.Clike or PowerShellWasmTokenKind.Cnotlike or
                PowerShellWasmTokenKind.Cmatch or PowerShellWasmTokenKind.Cnotmatch or PowerShellWasmTokenKind.Creplace or
                PowerShellWasmTokenKind.Ccontains or PowerShellWasmTokenKind.Cnotcontains or PowerShellWasmTokenKind.Cin or
                PowerShellWasmTokenKind.Cnotin or PowerShellWasmTokenKind.Csplit or PowerShellWasmTokenKind.Shl or
                PowerShellWasmTokenKind.Shr or PowerShellWasmTokenKind.Join => 6,
            PowerShellWasmTokenKind.Plus or PowerShellWasmTokenKind.Minus => 7,
            PowerShellWasmTokenKind.Star or PowerShellWasmTokenKind.Slash or PowerShellWasmTokenKind.Remainder => 8,
            _ => 0
        };

    public static bool IsCaseSensitive(this PowerShellWasmTokenKind kind) =>
        kind is PowerShellWasmTokenKind.Ceq or PowerShellWasmTokenKind.Cne or PowerShellWasmTokenKind.Cge or
            PowerShellWasmTokenKind.Cgt or PowerShellWasmTokenKind.Clt or PowerShellWasmTokenKind.Cle or
            PowerShellWasmTokenKind.Clike or PowerShellWasmTokenKind.Cnotlike or PowerShellWasmTokenKind.Cmatch or
            PowerShellWasmTokenKind.Cnotmatch or PowerShellWasmTokenKind.Creplace or PowerShellWasmTokenKind.Ccontains or
            PowerShellWasmTokenKind.Cnotcontains or PowerShellWasmTokenKind.Cin or PowerShellWasmTokenKind.Cnotin or
            PowerShellWasmTokenKind.Csplit;

    public static string Text(this PowerShellWasmTokenKind kind) =>
        kind switch
        {
            PowerShellWasmTokenKind.Remainder => "%",
            PowerShellWasmTokenKind.DotDot => "..",
            PowerShellWasmTokenKind.QuestionQuestion => "??",
            PowerShellWasmTokenKind.Format => "-f",
            PowerShellWasmTokenKind.Not => "-not",
            PowerShellWasmTokenKind.Bnot => "-bnot",
            PowerShellWasmTokenKind.And => "-and",
            PowerShellWasmTokenKind.Or => "-or",
            PowerShellWasmTokenKind.Xor => "-xor",
            PowerShellWasmTokenKind.Band => "-band",
            PowerShellWasmTokenKind.Bor => "-bor",
            PowerShellWasmTokenKind.Bxor => "-bxor",
            PowerShellWasmTokenKind.Join => "-join",
            PowerShellWasmTokenKind.Ieq => "-eq",
            PowerShellWasmTokenKind.Ine => "-ne",
            PowerShellWasmTokenKind.Ige => "-ge",
            PowerShellWasmTokenKind.Igt => "-gt",
            PowerShellWasmTokenKind.Ilt => "-lt",
            PowerShellWasmTokenKind.Ile => "-le",
            PowerShellWasmTokenKind.Ilike => "-like",
            PowerShellWasmTokenKind.Inotlike => "-notlike",
            PowerShellWasmTokenKind.Imatch => "-match",
            PowerShellWasmTokenKind.Inotmatch => "-notmatch",
            PowerShellWasmTokenKind.Ireplace => "-replace",
            PowerShellWasmTokenKind.Icontains => "-contains",
            PowerShellWasmTokenKind.Inotcontains => "-notcontains",
            PowerShellWasmTokenKind.Iin => "-in",
            PowerShellWasmTokenKind.Inotin => "-notin",
            PowerShellWasmTokenKind.Isplit => "-split",
            PowerShellWasmTokenKind.Ceq => "-ceq",
            PowerShellWasmTokenKind.Cne => "-cne",
            PowerShellWasmTokenKind.Cge => "-cge",
            PowerShellWasmTokenKind.Cgt => "-cgt",
            PowerShellWasmTokenKind.Clt => "-clt",
            PowerShellWasmTokenKind.Cle => "-cle",
            PowerShellWasmTokenKind.Clike => "-clike",
            PowerShellWasmTokenKind.Cnotlike => "-cnotlike",
            PowerShellWasmTokenKind.Cmatch => "-cmatch",
            PowerShellWasmTokenKind.Cnotmatch => "-cnotmatch",
            PowerShellWasmTokenKind.Creplace => "-creplace",
            PowerShellWasmTokenKind.Ccontains => "-ccontains",
            PowerShellWasmTokenKind.Cnotcontains => "-cnotcontains",
            PowerShellWasmTokenKind.Cin => "-cin",
            PowerShellWasmTokenKind.Cnotin => "-cnotin",
            PowerShellWasmTokenKind.Csplit => "-csplit",
            PowerShellWasmTokenKind.Shl => "-shl",
            PowerShellWasmTokenKind.Shr => "-shr",
            _ => kind.ToString()
        };
}
