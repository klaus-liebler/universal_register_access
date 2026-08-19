using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UniversalRegisterAccess.Parsing;

// Pure syntax-level attribute recognition (no semantic model/compilation) -- attributes are matched by
// their simple NAME text ("ModbusRegion", optionally "ModbusRegionAttribute"), deliberately loose,
// same rationale as BestBinaryBuffers.AttributeHelpers.
internal static class AttributeHelpers
{
    public static IEnumerable<AttributeSyntax> AllAttributes(this MemberDeclarationSyntax member) =>
        member.AttributeLists.SelectMany(list => list.Attributes);

    public static bool HasAttribute(this MemberDeclarationSyntax member, string shortName) =>
        member.AllAttributes().Any(a => NameMatches(a, shortName));

    public static AttributeSyntax? FindAttribute(this MemberDeclarationSyntax member, string shortName) =>
        member.AllAttributes().FirstOrDefault(a => NameMatches(a, shortName));

    private static bool NameMatches(AttributeSyntax attr, string shortName)
    {
        var text = attr.Name.ToString();
        var lastSegment = text[(text.LastIndexOf('.') + 1)..];
        return lastSegment == shortName || lastSegment == shortName + "Attribute";
    }

    public static Dictionary<string, ExpressionSyntax> ResolveArguments(AttributeSyntax attr, params string[] parameterNamesInOrder)
    {
        var result = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        if (attr.ArgumentList is null) return result;
        var positionalIndex = 0;
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals is not null)
            {
                result[arg.NameEquals.Name.Identifier.Text] = arg.Expression;
                continue;
            }
            if (arg.NameColon is not null)
            {
                result[arg.NameColon.Name.Identifier.Text] = arg.Expression;
                continue;
            }
            if (positionalIndex >= parameterNamesInOrder.Length)
            {
                throw new SchemaException($"UniversalRegisterAccess: zu viele Argumente im Attribut \"{attr}\".");
            }
            result[parameterNamesInOrder[positionalIndex]] = arg.Expression;
            positionalIndex++;
        }
        return result;
    }

    public static string? GetStringArgument(Dictionary<string, ExpressionSyntax> args, string name)
    {
        if (!args.TryGetValue(name, out var expr)) return null;
        if (expr is LiteralExpressionSyntax { Token.Value: string s }) return s;
        throw new SchemaException($"UniversalRegisterAccess: Attribut-Argument \"{name}\" muss ein String-Literal sein, war \"{expr}\".");
    }

    public static int? GetIntArgument(Dictionary<string, ExpressionSyntax> args, string name)
    {
        if (!args.TryGetValue(name, out var expr)) return null;
        return EvaluateIntLiteral(expr, name);
    }

    // [ModbusRegister(Access.ReadOnly)] -- matched by identifier text against Enum.GetNames, no
    // semantic model (same as BestBinaryBuffers.GetMessageKindArgument for MessageKind).
    public static Access GetAccessArgument(Dictionary<string, ExpressionSyntax> args, string name)
    {
        if (!args.TryGetValue(name, out var expr))
        {
            throw new SchemaException("UniversalRegisterAccess: [ModbusRegister] braucht ein \"access\"-Argument (Access.ReadOnly/ReadWrite).");
        }
        var text = expr switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => throw new SchemaException($"UniversalRegisterAccess: \"access\"-Argument von [ModbusRegister] konnte nicht gelesen werden: \"{expr}\"."),
        };
        if (!Enum.TryParse<Access>(text, out var access))
        {
            throw new SchemaException($"UniversalRegisterAccess: unbekannter Access-Wert \"{text}\" (gueltig: ReadOnly/ReadWrite).");
        }
        return access;
    }

    private static int EvaluateIntLiteral(ExpressionSyntax expr, string name) => expr switch
    {
        LiteralExpressionSyntax { Token.Value: int i } => i,
        PrefixUnaryExpressionSyntax { OperatorToken.Text: "-" } prefix => -EvaluateIntLiteral(prefix.Operand, name),
        _ => throw new SchemaException($"UniversalRegisterAccess: Attribut-Argument \"{name}\" muss ein Ganzzahl-Literal sein, war \"{expr}\"."),
    };
}
