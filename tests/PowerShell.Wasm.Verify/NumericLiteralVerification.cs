using PSWasm;

internal static class NumericLiteralVerification
{
    public static async ValueTask VerifyAsync()
    {
        // Literal values, native types, and rejected syntax were recorded with native PowerShell 7.6.1.
        // Numeric-looking command tokens invoke only functions defined inside each test script.
        var cases = new (string Name, string Script, PowerShellWasmOutputRecord[] Expected)[]
        {
            ("numeric_literal_001", """
$n=0;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=Int32")]),
            ("numeric_literal_002", """
$n=1;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int32")]),
            ("numeric_literal_003", """
$n=2147483647;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2147483647;type=Int32")]),
            ("numeric_literal_004", """
$n=2147483648;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2147483648;type=Int64")]),
            ("numeric_literal_005", """
$n=9223372036854775807;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=9223372036854775807;type=Int64")]),
            ("numeric_literal_006", """
$n=9223372036854775808;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=9223372036854775808;type=Decimal")]),
            ("numeric_literal_007", """
$n=18446744073709551615;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=18446744073709551615;type=Decimal")]),
            ("numeric_literal_008", """
$n=18446744073709551616;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=18446744073709551616;type=Decimal")]),
            ("numeric_literal_009", """
$n=79228162514264337593543950335;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=79228162514264337593543950335;type=Decimal")]),
            ("numeric_literal_010", """
$n=79228162514264337593543950336;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=7.92281625142643E+28;type=Double")]),
            ("numeric_literal_011", """
$n=1y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=SByte")]),
            ("numeric_literal_012", """
$n=127y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=127;type=SByte")]),
            ("numeric_literal_014", """
$n=-128y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-128;type=SByte")]),
            ("numeric_literal_016", """
$n=1uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Byte")]),
            ("numeric_literal_017", """
$n=255uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=Byte")]),
            ("numeric_literal_020", """
$n=1s;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int16")]),
            ("numeric_literal_021", """
$n=32767s;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=32767;type=Int16")]),
            ("numeric_literal_023", """
$n=-32768s;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-32768;type=Int16")]),
            ("numeric_literal_024", """
$n=1us;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=UInt16")]),
            ("numeric_literal_025", """
$n=65535us;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=65535;type=UInt16")]),
            ("numeric_literal_028", """
$n=1u;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=UInt32")]),
            ("numeric_literal_029", """
$n=4294967295u;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967295;type=UInt32")]),
            ("numeric_literal_030", """
$n=4294967296u;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967296;type=UInt64")]),
            ("numeric_literal_032", """
$n=1l;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int64")]),
            ("numeric_literal_033", """
$n=1ul;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=UInt64")]),
            ("numeric_literal_034", """
$n=18446744073709551615ul;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=18446744073709551615;type=UInt64")]),
            ("numeric_literal_037", """
$n=1d;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Decimal")]),
            ("numeric_literal_038", """
$n=1.20d;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1.20;type=Decimal")]),
            ("numeric_literal_039", """
$n=1n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=BigInteger")]),
            ("numeric_literal_040", """
$n=123456789012345678901234567890123456789n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=123456789012345678901234567890123456789;type=BigInteger")]),
            ("numeric_literal_041", """
$n=0xFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=Int32")]),
            ("numeric_literal_042", """
$n=0x7FFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2147483647;type=Int32")]),
            ("numeric_literal_043", """
$n=0x80000000;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-2147483648;type=Int32")]),
            ("numeric_literal_044", """
$n=0xFFFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_literal_045", """
$n=0x00000000FFFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967295;type=Int64")]),
            ("numeric_literal_046", """
$n=0x7FFFFFFFFFFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=9223372036854775807;type=Int64")]),
            ("numeric_literal_047", """
$n=0x8000000000000000;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-9223372036854775808;type=Int64")]),
            ("numeric_literal_048", """
$n=0xFFFFFFFFFFFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int64")]),
            ("numeric_literal_050", """
$n=0xFFy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=SByte")]),
            ("numeric_literal_051", """
$n=0x80y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-128;type=SByte")]),
            ("numeric_literal_052", """
$n=0xFFuy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=Byte")]),
            ("numeric_literal_053", """
$n=0xFFFFs;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int16")]),
            ("numeric_literal_054", """
$n=0xFFFFus;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=65535;type=UInt16")]),
            ("numeric_literal_055", """
$n=0xFFFFFFFFu;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967295;type=UInt32")]),
            ("numeric_literal_056", """
$n=0xFFFFFFFFl;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967295;type=Int64")]),
            ("numeric_literal_057", """
$n=0xFFFFFFFFul;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4294967295;type=UInt64")]),
            ("numeric_literal_058", """
$n=0xFFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=BigInteger")]),
            ("numeric_literal_059", """
$n=0x0FFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=BigInteger")]),
            ("numeric_literal_060", """
$n=0b1010;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=10;type=Int32")]),
            ("numeric_literal_061", """
$n=0b11111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_literal_062", """
$n=0b11111111y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=SByte")]),
            ("numeric_literal_063", """
$n=0b10000000y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-128;type=SByte")]),
            ("numeric_literal_064", """
$n=0b11111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_literal_065", """
$n=.5;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0.5;type=Double")]),
            ("numeric_literal_066", """
$n=1.;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Double")]),
            ("numeric_literal_067", """
$n=1.5;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1.5;type=Double")]),
            ("numeric_literal_068", """
$n=1e2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=100;type=Double")]),
            ("numeric_literal_069", """
$n=1e-2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0.01;type=Double")]),
            ("numeric_literal_070", """
$n=1e+2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=100;type=Double")]),
            ("numeric_literal_071", """
$n=1.5e2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=150;type=Double")]),
            ("numeric_literal_072", """
$n=1e2d;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=100;type=Decimal")]),
            ("numeric_literal_073", """
$n=1e2n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=100;type=BigInteger")]),
            ("numeric_literal_074", """
$n=1kb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1024;type=Int32")]),
            ("numeric_literal_075", """
$n=1mb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1048576;type=Int32")]),
            ("numeric_literal_076", """
$n=1gb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1073741824;type=Int32")]),
            ("numeric_literal_077", """
$n=1tb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1099511627776;type=Int64")]),
            ("numeric_literal_078", """
$n=1pb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1125899906842624;type=Int64")]),
            ("numeric_literal_079", """
$n=1.5kb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1536;type=Double")]),
            ("numeric_literal_080", """
$n=1dkb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1024;type=Decimal")]),
            ("numeric_literal_081", """
$n=1ukb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1024;type=UInt32")]),
            ("numeric_literal_083", """
function 1kby {'local-command'};$n=1kby;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_literal_084", """
$n=-1y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=SByte")]),
            ("numeric_literal_085", """
$n=- 1y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_literal_086", """
$n=+1y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=SByte")]),
            ("numeric_literal_087", """
$n=+ 1y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int32")]),
            ("numeric_literal_088", """
$n=-2147483648;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-2147483648;type=Int32")]),
            ("numeric_literal_089", """
$n=- 2147483648;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-2147483648;type=Int64")]),
            ("numeric_literal_090", """
$n=-0x80000000;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2147483648;type=Int64")]),
            ("numeric_literal_091", """
$n=- 0x80000000;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2147483648;type=Double")]),
            ("numeric_literal_092", """
$n=+1uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Byte")]),
            ("numeric_literal_093", """
$n=+ 1uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int32")]),
            ("numeric_literal_094", """
$n=- 1uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_literal_095", """
$n=-9223372036854775808;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-9223372036854775808;type=Int64")]),
            ("numeric_literal_096", """
$n=- 9223372036854775808;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-9223372036854775808;type=Decimal")]),
            ("numeric_extended_001", """
$n=0xFFFFFFFFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_002", """
$n=0xFFFFFFFFFFFFFFFFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_003", """
$n=0xFFFFFFFFFFFFFFFFFFFFFFFFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=79228162514264337593543950335;type=BigInteger")]),
            ("numeric_extended_004", """
$n=0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFn;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=340282366920938463463374607431768211455;type=BigInteger")]),
            ("numeric_extended_005", """
$n=0xFFFFFFFFFFFFFFFu;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1152921504606846975;type=UInt64")]),
            ("numeric_extended_006", """
$n=0x0FFFFFFFFFFFFFFFu;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1152921504606846975;type=UInt64")]),
            ("numeric_extended_007", """
$n=0x0000000000000001u;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=UInt32")]),
            ("numeric_extended_008", """
$n=1.5l;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2;type=Int64")]),
            ("numeric_extended_009", """
$n=2.5l;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2;type=Int64")]),
            ("numeric_extended_010", """
$n=3.5y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=4;type=SByte")]),
            ("numeric_extended_011", """
$n=-1.5l;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-2;type=Int64")]),
            ("numeric_extended_012", """
$n=1.5lkb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1536;type=Int64")]),
            ("numeric_extended_013", """
$n=0.001lkb;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int64")]),
            ("numeric_extended_014", """
$n=-0.5u;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=UInt32")]),
            ("numeric_extended_015", """
$n=-0.1uy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=Byte")]),
            ("numeric_extended_016", """
$n=1e309;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=Infinity;type=Double")]),
            ("numeric_extended_017", """
$n=-1e309;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-Infinity;type=Double")]),
            ("numeric_extended_018", """
$n=1e-999;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=Double")]),
            ("numeric_extended_020", """
$n=1e9999;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=Infinity;type=Double")]),
            ("numeric_extended_021", """
$n=0123;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=123;type=Int32")]),
            ("numeric_extended_022", """
function 0b2 {'local-command'};$n=0b2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_023", """
function 0b {'local-command'};$n=0b;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_024", """
$n=0x;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=Int32")]),
            ("numeric_extended_025", """
function 0xG1 {'local-command'};$n=0xG1;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_026", """
function 1_000 {'local-command'};$n=1_000;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_027", """
function 1f {'local-command'};$n=1f;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_028", """
function 1m {'local-command'};$n=1m;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_029", """
function 1e {'local-command'};$n=1e;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_030", """
function 1e`+ {'local-command'};$n=1e+;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_031", """
function 1e`+2foo {'local-command'};$n=1e+2foo;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_032", """
function 0x1p2 {'local-command'};$n=0x1p2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_033", """
function 1gbus {'local-command'};$n=1gbus;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_extended_034", """
$n=.5d;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0.5;type=Decimal")]),
            ("numeric_extended_035", """
$n=-.5;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-0.5;type=Double")]),
            ("numeric_extended_036", """
$n=+.5;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0.5;type=Double")]),
            ("numeric_extended_037", """
$n=-.5d;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-0.5;type=Decimal")]),
            ("numeric_extended_038", """
$n=1.e2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=100;type=Double")]),
            ("numeric_extended_039", """
$n=1.e-2;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0.01;type=Double")]),
            ("numeric_extended_040", """
$n=0b11111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_041", """
$n=0b11111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_042", """
$n=0b1111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_043", """
$n=0b1111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_044", """
$n=0b111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=16777215;type=Int32")]),
            ("numeric_extended_045", """
$n=0b111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=16777215;type=BigInteger")]),
            ("numeric_extended_046", """
$n=0b11111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_047", """
$n=0b11111111111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_048", """
$n=0b1111111111111111111111111111111111111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int64")]),
            ("numeric_extended_049", """
$n=0b1111111111111111111111111111111111111111111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_050", """
$n=0b111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_051", """
$n=0b111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_052", """
$n=0b11111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_053", """
$n=0b11111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_extended_054", """
$n=0b1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_extended_055", """
$n=0b1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-1;type=BigInteger")]),
            ("numeric_argument_01", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 128y
""", [new("Output", "value=128y;type=String")]),
            ("numeric_argument_02", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1e9999
""", [new("Output", "value=Infinity;type=Double")]),
            ("numeric_argument_03", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1ykb
""", [new("Output", "value=1ykb;type=String")]),
            ("numeric_argument_04", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1gbus
""", [new("Output", "value=1gbus;type=String")]),
            ("numeric_argument_05", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N .5
""", [new("Output", "value=0.5;type=Double")]),
            ("numeric_argument_06", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 0xFFFFFFFF
""", [new("Output", "value=-1;type=Int32")]),
            ("numeric_argument_07", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N -1y
""", [new("Output", "value=-1y;type=String")]),
            ("numeric_argument_08", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N +1y
""", [new("Output", "value=+1y;type=String")]),
            ("numeric_argument_09", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N -2
""", [new("Output", "value=-2;type=String")]),
            ("numeric_argument_10", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N +2
""", [new("Output", "value=+2;type=String")]),
            ("numeric_argument_11", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1u
""", [new("Output", "value=1;type=UInt32")]),
            ("numeric_argument_12", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1l
""", [new("Output", "value=1;type=Int64")]),
            ("numeric_argument_13", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N -1u
""", [new("Output", "value=-1u;type=String")]),
            ("numeric_argument_14", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N -1uy
""", [new("Output", "value=-1uy;type=String")]),
            ("numeric_argument_15", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 1e
""", [new("Output", "value=1e;type=String")]),
            ("numeric_argument_16", """
function N {foreach($a in $args){"value=$a;type=$($a.GetType().Name)"}};N 0xG1
""", [new("Output", "value=0xG1;type=String")]),
            ("numeric_ambiguity_01", """
try{$n=-1y.GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=SByte;type=String")]),
            ("numeric_ambiguity_02", """
try{$n=+1y.GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=SByte;type=String")]),
            ("numeric_ambiguity_03", """
try{$n=- 1y.GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "caught")]),
            ("numeric_ambiguity_04", """
try{$n=(-1y).GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=SByte;type=String")]),
            ("numeric_ambiguity_05", """
try{$n=-.5.GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=Double;type=String")]),
            ("numeric_ambiguity_06", """
try{$n=-0x80000000.GetType().Name;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=Int64;type=String")]),
            ("numeric_ambiguity_07", """
try{$n=1..3;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1,2,3;type=Object[]")]),
            ("numeric_ambiguity_08", """
try{$n=1.5..3.5;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=2,3,4;type=Object[]")]),
            ("numeric_ambiguity_09", """
try{$n=0x1..0x3;"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1,2,3;type=Object[]")]),
            ("numeric_expression_01", """
try{$n=1y+2y;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=3;type=Int32")]),
            ("numeric_expression_02", """
try{$n=255uy+1;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=256;type=Int32")]),
            ("numeric_expression_03", """
try{$n=1u+2u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=3;type=UInt32")]),
            ("numeric_expression_04", """
try{$n=4294967295u+1;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=4294967296;type=Double")]),
            ("numeric_expression_05", """
try{$n=18446744073709551615ul+1;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1.84467440737096E+19;type=Double")]),
            ("numeric_expression_06", """
try{$n=1ul+1;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=2;type=UInt64")]),
            ("numeric_expression_07", """
try{$n=1n+2;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=3;type=BigInteger")]),
            ("numeric_expression_08", """
try{$n=1n+0.5;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1;type=BigInteger")]),
            ("numeric_expression_09", """
try{$n=5n/2n;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=2;type=BigInteger")]),
            ("numeric_expression_10", """
try{$n=5n%2n;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1;type=BigInteger")]),
            ("numeric_expression_11", """
try{$n=1.0d+1n;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=2.0;type=Decimal")]),
            ("numeric_expression_12", """
try{$n=1u-2u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=-1;type=Double")]),
            ("numeric_expression_13", """
try{$n=0ul-1ul;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=-1;type=Double")]),
            ("numeric_expression_14", """
try{$n=-bnot 0u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=4294967295;type=UInt32")]),
            ("numeric_expression_15", """
try{$n=-bnot 0ul;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=18446744073709551615;type=UInt64")]),
            ("numeric_expression_16", """
try{$n=1u -shl 31;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=2147483648;type=UInt32")]),
            ("numeric_expression_17", """
try{$n=1n -shl 64;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=18446744073709551616;type=BigInteger")]),
            ("numeric_expression_18", """
try{$n=0x000000000000001u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1;type=UInt64")]),
            ("numeric_expression_19", """
try{$n=0x0000000000000001u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1;type=UInt32")]),
            ("numeric_expression_20", """
try{$n=0x00000000000000001u;"value=$n;type=$($n.GetType().Name)"}catch{'caught'}
""", [new("Output", "value=1;type=UInt32")]),
            ("numeric_zero_bool", """
$values=0y,0uy,0s,0us,0u,0l,0ul,0d,0n;foreach($n in $values){"type=$($n.GetType().Name);bool=$([bool]$n)"}
""", [new("Output", "type=SByte;bool=False"), new("Output", "type=Byte;bool=False"), new("Output", "type=Int16;bool=False"), new("Output", "type=UInt16;bool=False"), new("Output", "type=UInt32;bool=False"), new("Output", "type=Int64;bool=False"), new("Output", "type=UInt64;bool=False"), new("Output", "type=Decimal;bool=False"), new("Output", "type=BigInteger;bool=False")]),
            ("numeric_nonzero_bool", """
$values=1y,1uy,1s,1us,1u,1l,1ul,1d,1n;foreach($n in $values){"type=$($n.GetType().Name);bool=$([bool]$n)"}
""", [new("Output", "type=SByte;bool=True"), new("Output", "type=Byte;bool=True"), new("Output", "type=Int16;bool=True"), new("Output", "type=UInt16;bool=True"), new("Output", "type=UInt32;bool=True"), new("Output", "type=Int64;bool=True"), new("Output", "type=UInt64;bool=True"), new("Output", "type=Decimal;bool=True"), new("Output", "type=BigInteger;bool=True")]),
            ("numeric_exact_comparisons", """
"n=$((9007199254740993n) -eq (9007199254740992n));ul=$((18446744073709551615ul) -eq (18446744073709551614ul));mixed=$((18446744073709551615ul) -eq (18446744073709551615n));less=$((9007199254740992n) -lt (9007199254740993n))"
""", [new("Output", "n=False;ul=False;mixed=True;less=True")]),
            ("numeric_cast_unsigned", """
$n=[uint64]18446744073709551615n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=18446744073709551615;type=UInt64")]),
            ("numeric_cast_bigint", """
$n=[bigint]18446744073709551615ul;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=18446744073709551615;type=BigInteger")]),
            ("numeric_cast_bigint_string", """
$n=[bigint]'123456789012345678901234567890123456789';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=123456789012345665810180280328680112128;type=BigInteger")]),
            ("numeric_cast_bigint_overflow", """
try{$n=[uint64]18446744073709551616n}catch{'caught'};'after'
""", [new("Output", "caught"), new("Output", "after")]),
            ("numeric_increment_types", """
$u=4294967295u;$u++;"u=$u;type=$($u.GetType().Name)";$n=18446744073709551615n;try{$n++}catch{'bigint-increment-rejected'};"n=$n;type=$($n.GetType().Name)"
""", [new("Output", "u=4294967296;type=Double"), new("Output", "bigint-increment-rejected"), new("Output", "n=18446744073709551615;type=BigInteger")]),
            ("numeric_format_interpolation", """
$n=1.2345678901234567;"interpolation=$n";"cast=$([string]$n)";"method=$($n.ToString())"
""", [new("Output", "interpolation=1.23456789012346"), new("Output", "cast=1.23456789012346"), new("Output", "method=1.2345678901234567")]),
            ("numeric_format_raw", """
1.2345678901234567
""", [new("Output", "1.23456789012346")]),
            ("numeric_format_bigdouble", """
$n=79228162514264337593543950336;"interpolation=$n";"cast=$([string]$n)";"method=$($n.ToString())"
""", [new("Output", "interpolation=7.92281625142643E+28"), new("Output", "cast=7.92281625142643E+28"), new("Output", "method=7.922816251426434E+28")]),
            ("numeric_json_uint64", """
18446744073709551615ul | ConvertTo-Json -Compress
""", [new("Output", "18446744073709551615")]),
            ("numeric_json_decimal", """
79228162514264337593543950335 | ConvertTo-Json -Compress
""", [new("Output", "79228162514264337593543950335.0")]),
            ("numeric_json_bigint", """
123456789012345678901234567890123456789n | ConvertTo-Json -Compress
""", [new("Output", "123456789012345678901234567890123456789")]),
            ("numeric_boundary_01", """
$n=0b1111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=127;type=Int32")]),
            ("numeric_boundary_02", """
$n=0b011111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=Int32")]),
            ("numeric_boundary_03", """
$n=-0b11111111;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int32")]),
            ("numeric_boundary_04", """
$n=-0xFFy;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=SByte")]),
            ("numeric_boundary_06", """
$n=- 0x80y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=128;type=Int32")]),
            ("numeric_boundary_07", """
$n=-0xFFFFFFFF;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1;type=Int32")]),
            ("numeric_boundary_08", """
$n=-0xFFFFFFFFl;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=-4294967295;type=Int64")]),
            ("numeric_boundary_10", """
function 1lu {'local-command'};$n=1lu;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_boundary_11", """
function 1un {'local-command'};$n=1un;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=local-command;type=String")]),
            ("numeric_boundary_12", """
$n=0XffUL;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=255;type=UInt64")]),
            ("numeric_boundary_13", """
$n=0B101UY;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=5;type=Byte")]),
            ("numeric_boundary_14", """
$n=1DkB;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1024;type=Decimal")]),
            ("numeric_boundary_15", """
$n=.5y;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=SByte")]),
            ("numeric_boundary_16", """
$n=.5n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=BigInteger")]),
            ("numeric_boundary_17", """
$n=2.5n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=2;type=BigInteger")]),
            ("numeric_boundary_18", """
$n=1e-2n;"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=0;type=BigInteger")]),
            ("numeric_type_checks", """
"sbyte=$((1y) -is [sbyte]);byte=$((1uy) -is [byte]);short=$((1s) -is [short]);ushort=$((1us) -is [ushort]);uint=$((1u) -is [uint]);ulong=$((1ul) -is [ulong]);big=$((1n) -is [bigint]);notInt=$((1y) -isnot [int])"
""", [new("Output", "sbyte=True;byte=True;short=True;ushort=True;uint=True;ulong=True;big=True;notInt=True")]),
            ("numeric_typed_small_assignment", """
[sbyte]$n=1y;$n+=2;"value=$n;type=$($n.GetType().Name)";try{$n=128}catch{'caught'};"after=$n"
""", [new("Output", "value=3;type=SByte"), new("Output", "caught"), new("Output", "after=3")]),
            ("numeric_typed_unsigned_assignment", """
[ulong]$n=18446744073709551614ul;$n=18446744073709551615n;"value=$n;type=$($n.GetType().Name)";try{$n=-1}catch{'caught'};"after=$n"
""", [new("Output", "value=18446744073709551615;type=UInt64"), new("Output", "caught"), new("Output", "after=18446744073709551615")]),
            ("numeric_json_mixed_types", """
@(1y,2uy,3s,4us,5u,6ul,7n) | ConvertTo-Json -Compress
""", [new("Output", "[1,2,3,4,5,6,7]")]),
            ("numeric_json_finite_markers", """
@(1.0,1d,1.20d,1e2,1e20) | ConvertTo-Json -Compress
""", [new("Output", "[1.0,1.0,1.20,100.0,1E+20]")]),
            ("numeric_json_nonfinite_signed_zero", """
@(1e309,-1e309,-0.0) | ConvertTo-Json -Compress
""", [new("Output", "[\"Infinity\",\"-Infinity\",-0.0]")]),
            ("numeric_decimal_string_precision", """
$n=[decimal]'1.2345678901234567890123456789';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1.23456789012346;type=Decimal")]),
            ("numeric_decimal_string_scale", """
$n=[decimal]'1.2300';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1.23;type=Decimal")]),
            ("numeric_decimal_string_suffix", """
$n=[decimal]'1.234567890123456789d';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1.234567890123456789;type=Decimal")]),
            ("numeric_decimal_grouped_string", """
$n=[decimal]'1,234.567890123456789';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=1234.567890123456789;type=Decimal")]),
            ("numeric_bigint_string_decimal_range", """
$n=[bigint]'12345678901234567890123456789';"value=$n;type=$($n.GetType().Name)"
""", [new("Output", "value=12345678901234567890123456789;type=BigInteger")]),
            ("numeric_ushort_collection_mutations", """
$c=(1,2).ForEach([ushort]);$c.Add('65535');$c.Insert(1,'123');$c[0]='32768';$c.Count;foreach($n in $c){"value=$n;type=$($n.GetType().Name)"};try{$c[0]='65536'}catch{'range-caught'};"preserved=$($c[0])"
""", [new("Output", "4"), new("Output", "value=32768;type=UInt16"), new("Output", "value=123;type=UInt16"), new("Output", "value=2;type=UInt16"), new("Output", "value=65535;type=UInt16"), new("Output", "range-caught"), new("Output", "preserved=32768")]),
            ("numeric_ulong_collection_mutations", """
$c=(1,2).ForEach([ulong]);$c.Add('18446744073709551615');$c.Insert(1,'9223372036854775808');$c[0]='4294967296';$c.Count;foreach($n in $c){"value=$n;type=$($n.GetType().Name)"};try{$c[0]='-1'}catch{'range-caught'};"preserved=$($c[0])"
""", [new("Output", "4"), new("Output", "value=4294967296;type=UInt64"), new("Output", "value=9223372036854775808;type=UInt64"), new("Output", "value=2;type=UInt64"), new("Output", "value=18446744073709551615;type=UInt64"), new("Output", "range-caught"), new("Output", "preserved=4294967296")]),
            ("numeric_bigint_collection_mutations", """
$c=(1,2).ForEach([bigint]);$c.Add('12345678901234567890123456789');$c.Insert(1,'9223372036854775808');$c[0]='18446744073709551615ul';$c.Count;foreach($n in $c){"value=$n;type=$($n.GetType().Name)"};try{$c[0]='not-a-number'}catch{'conversion-caught'};"preserved=$($c[0])"
""", [new("Output", "4"), new("Output", "value=18446744073709551615;type=BigInteger"), new("Output", "value=9223372036854775808;type=BigInteger"), new("Output", "value=2;type=BigInteger"), new("Output", "value=12345678901234567890123456789;type=BigInteger"), new("Output", "conversion-caught"), new("Output", "preserved=18446744073709551615")]),
            ("numeric_ulong_sort_exact", """
$sorted=@(18446744073709551615ul,18446744073709551614ul,9223372036854775808ul|Sort-Object);foreach($n in $sorted){"value=$n;type=$($n.GetType().Name)"}
""", [new("Output", "value=9223372036854775808;type=UInt64"), new("Output", "value=18446744073709551614;type=UInt64"), new("Output", "value=18446744073709551615;type=UInt64")]),
            ("numeric_bigint_decimal_sort_exact", """
$sorted=@(9007199254740993n,9007199254740992d,9007199254740994ul,9007199254740991l|Sort-Object);foreach($n in $sorted){"value=$n;type=$($n.GetType().Name)"}
""", [new("Output", "value=9007199254740991;type=Int64"), new("Output", "value=9007199254740992;type=Decimal"), new("Output", "value=9007199254740993;type=BigInteger"), new("Output", "value=9007199254740994;type=UInt64")]),
            ("numeric_long_overflow_roundtrip", """
([long]::MaxValue+2048).ToString('R')
""", [new("Output", "9.223372036854778E+18")]),
            ("numeric_int_product_roundtrip", """
(1915967687*1834717116).ToString('R')
""", [new("Output", "3.515258709041831E+18")]),
            ("numeric_uint_product_roundtrip", """
(3831935375u*3669434232u).ToString('R')
""", [new("Output", "1.4061034839836758E+19")]),
            ("numeric_typed_array_index_conversion", """
$a=[uint[]]@(1,2);$a[0]='3';"value=$($a[0]);type=$($a[0].GetType().Name)";try{$a[0]='-1'}catch{'range-caught'};"preserved=$($a[0])";$b=[ulong[]]@(1,2);$b[0]='18446744073709551615';"value=$($b[0]);type=$($b[0].GetType().Name)";$c=[bigint[]]@(1,2);$c[0]='12345678901234567890123456789';"value=$($c[0]);type=$($c[0].GetType().Name)"
""", [new("Output", "value=3;type=UInt32"), new("Output", "range-caught"), new("Output", "preserved=3"), new("Output", "value=18446744073709551615;type=UInt64"), new("Output", "value=12345678901234567890123456789;type=BigInteger")]),
            ("numeric_integer_hex_format", """
(-1).ToString('X');(4294967295u).ToString('X');(18446744073709551615ul).ToString('X')
""", [new("Output", "FFFFFFFF"), new("Output", "FFFFFFFF"), new("Output", "FFFFFFFFFFFFFFFF")]),
            ("numeric_static_bounds", """
$values=@([sbyte]::MinValue,[sbyte]::MaxValue,[byte]::MinValue,[byte]::MaxValue,[short]::MinValue,[short]::MaxValue,[ushort]::MinValue,[ushort]::MaxValue,[int]::MinValue,[int]::MaxValue,[uint]::MinValue,[uint]::MaxValue,[long]::MinValue,[long]::MaxValue,[ulong]::MinValue,[ulong]::MaxValue,[single]::MinValue,[single]::MaxValue,[double]::MinValue,[double]::MaxValue,[decimal]::MinValue,[decimal]::MaxValue);foreach($n in $values){"value=$n;type=$($n.GetType().Name)"}
""", [new("Output", "value=-128;type=SByte"), new("Output", "value=127;type=SByte"), new("Output", "value=0;type=Byte"), new("Output", "value=255;type=Byte"), new("Output", "value=-32768;type=Int16"), new("Output", "value=32767;type=Int16"), new("Output", "value=0;type=UInt16"), new("Output", "value=65535;type=UInt16"), new("Output", "value=-2147483648;type=Int32"), new("Output", "value=2147483647;type=Int32"), new("Output", "value=0;type=UInt32"), new("Output", "value=4294967295;type=UInt32"), new("Output", "value=-9223372036854775808;type=Int64"), new("Output", "value=9223372036854775807;type=Int64"), new("Output", "value=0;type=UInt64"), new("Output", "value=18446744073709551615;type=UInt64"), new("Output", "value=-3.402823E+38;type=Single"), new("Output", "value=3.402823E+38;type=Single"), new("Output", "value=-1.79769313486232E+308;type=Double"), new("Output", "value=1.79769313486232E+308;type=Double"), new("Output", "value=-79228162514264337593543950335;type=Decimal"), new("Output", "value=79228162514264337593543950335;type=Decimal")]),
            ("numeric_signed_unsigned_array_identity", """
$a=[uint[]]@(1,2);"source=$($a.GetType().Name);isuint=$($a -is [uint[]]);isint=$($a -is [int[]])";$b=[int[]]$a;$b[0]='3';"cast=$($b.GetType().Name);shared=$($a[0]);item=$($b[0].GetType().Name)";$r=$a*2;"repeat=$($r.GetType().Name);count=$($r.Count)";$a=[sbyte[]]@(1,2);"source=$($a.GetType().Name);issbyte=$($a -is [sbyte[]]);isbyte=$($a -is [byte[]])";$b=[byte[]]$a;$b[0]='-1';"cast=$($b.GetType().Name);shared=$($a[0]);item=$($b[0].GetType().Name)";$r=$a*2;"repeat=$($r.GetType().Name);count=$($r.Count)";$a=[ushort[]]@(1,2);"source=$($a.GetType().Name);isushort=$($a -is [ushort[]]);isshort=$($a -is [short[]])";$b=[short[]]$a;$b[0]='65535';"cast=$($b.GetType().Name);shared=$($a[0]);item=$($b[0].GetType().Name)";$r=$a*2;"repeat=$($r.GetType().Name);count=$($r.Count)"
""", [new("Output", "source=UInt32[];isuint=True;isint=False"), new("Output", "cast=UInt32[];shared=3;item=UInt32"), new("Output", "repeat=UInt32[];count=4"), new("Output", "source=SByte[];issbyte=True;isbyte=False"), new("Output", "cast=SByte[];shared=-1;item=SByte"), new("Output", "repeat=SByte[];count=4"), new("Output", "source=UInt16[];isushort=True;isshort=False"), new("Output", "cast=UInt16[];shared=65535;item=UInt16"), new("Output", "repeat=UInt16[];count=4")]),
        };
        var diagnostics = new (string Name, string Script, string NativeIds, string ErrorPattern)[]
        {
            ("numeric_literal_013", """
$n=128y;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_015", """
$n=-129y;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_018", """
$n=256uy;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_019", """
$n=-1uy;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_022", """
$n=32768s;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_026", """
$n=65536us;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_027", """
$n=-1us;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_031", """
$n=-1u;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_035", """
$n=18446744073709551616ul;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_036", """
$n=-1ul;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_049", """
$n=0x10000000000000000;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_literal_082", """
$n=1ykb;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_extended_019", """
$n=1e309d;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_ambiguity_10", """
try{$n=1.ToString();"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", "ExpectedExpression", "expression|token|member"),
            ("numeric_ambiguity_11", """
try{$n=1..ToString();"value=$($n -join ',');type=$($n.GetType().Name)"}catch{'caught'}
""", "ExpectedValueExpression,UnexpectedToken,ExpectedExpression", "expression|token|member"),
            ("numeric_boundary_05", """
$n=-0x80y;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_boundary_09", """
$n=18446744073709551616u;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
            ("numeric_boundary_19", """
$n=-0xFFFFFFFFu;"value=$n;type=$($n.GetType().Name)"
""", "BadNumericConstant", "numeric|number|constant"),
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
        Console.WriteLine($"Numeric literal scenarios: {cases.Length - failures.Count}/{cases.Length} passed.");
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
        Console.WriteLine($"Numeric literal diagnostics: {diagnostics.Length - (failures.Count - outputFailures)}/{diagnostics.Length} passed.");
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
