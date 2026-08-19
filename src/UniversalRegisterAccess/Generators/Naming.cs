using System.Text;

namespace UniversalRegisterAccess.Generators;

internal static class Naming
{
    // "ChipId" -> "CHIP_ID", "CpuTemperatureC" -> "CPU_TEMPERATURE_C", "Stepper1Position" ->
    // "STEPPER1_POSITION" -- standard PascalCase-to-SCREAMING_SNAKE_CASE, splitting on
    // lowercase/digit-to-uppercase transitions and on the last letter of an uppercase run before a
    // new word starts (e.g. "HTTPServer" -> "HTTP_SERVER").
    public static string ScreamingSnake(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                var prev = pascal[i - 1];
                var prevIsLowerOrDigit = char.IsLower(prev) || char.IsDigit(prev);
                var nextIsLower = i + 1 < pascal.Length && char.IsLower(pascal[i + 1]);
                if (prevIsLowerOrDigit || (char.IsUpper(prev) && nextIsLower))
                {
                    sb.Append('_');
                }
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string CamelCase(string pascal) => pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
}
