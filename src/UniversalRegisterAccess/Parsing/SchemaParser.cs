using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Parsing;

/// <summary>Reads a set of C# schema files (syntax-only Roslyn parsing -- no Compilation/semantic model,
/// same rationale as BestBinaryBuffers) and produces the region/register model that the address-
/// assignment pass and the generators consume. Does NOT assign Address/NodeId -- see AddressAssignment.</summary>
public static class SchemaParser
{
    public static IReadOnlyList<RegionDef> Parse(IReadOnlyList<string> sourceFiles)
    {
        var bitfieldSyntax = new Dictionary<string, EnumDeclarationSyntax>(StringComparer.Ordinal);
        var structSyntax = new Dictionary<string, StructDeclarationSyntax>(StringComparer.Ordinal);
        var regionSyntax = new List<(ClassDeclarationSyntax Syntax, string Path)>();

        foreach (var path in sourceFiles)
        {
            var tree = ParseFile(path);
            foreach (var member in tree.GetCompilationUnitRoot().Members)
            {
                CollectDeclarations(member, path, bitfieldSyntax, structSyntax, regionSyntax);
            }
        }

        var bitfieldCache = new Dictionary<string, BitfieldTypeDef>(StringComparer.Ordinal);
        var structCache = new Dictionary<string, StructTypeDef>(StringComparer.Ordinal);

        BitfieldTypeDef ResolveBitfield(string name, string usedFrom)
        {
            if (bitfieldCache.TryGetValue(name, out var cached)) return cached;
            if (!bitfieldSyntax.TryGetValue(name, out var syntax))
            {
                throw new SchemaException($"{usedFrom}: unbekanntes [ModbusType]-Enum \"{name}\".");
            }
            var bits = new List<BitValue>();
            long next = 0;
            foreach (var member in syntax.Members)
            {
                var value = member.EqualsValue is not null ? EvaluateIntLiteral(member.EqualsValue.Value) : next;
                bits.Add(new BitValue(member.Identifier.Text, (int)value));
                next = value + 1;
            }
            var def = new BitfieldTypeDef(name, DocComments.Extract(syntax), bits);
            bitfieldCache[name] = def;
            return def;
        }

        StructTypeDef ResolveStruct(string name, string usedFrom)
        {
            if (structCache.TryGetValue(name, out var cached)) return cached;
            if (!structSyntax.TryGetValue(name, out var syntax))
            {
                throw new SchemaException($"{usedFrom}: unbekannter [ModbusType]-Struct \"{name}\".");
            }
            var fields = new List<StructWordField>();
            foreach (var member in syntax.Members)
            {
                if (member is not FieldDeclarationSyntax fieldDecl)
                {
                    throw new SchemaException($"[ModbusType] struct \"{name}\": nur Felder erlaubt, unerwartetes Member \"{member}\".");
                }
                var (isSigned, keyword) = ResolveWordFieldType(fieldDecl.Declaration.Type, name);
                foreach (var declarator in fieldDecl.Declaration.Variables)
                {
                    fields.Add(new StructWordField(declarator.Identifier.Text, DocComments.Extract(fieldDecl), isSigned));
                }
                _ = keyword;
            }
            if (fields.Count == 0)
            {
                throw new SchemaException($"[ModbusType] struct \"{name}\": braucht mindestens ein Feld.");
            }
            var def = new StructTypeDef(name, DocComments.Extract(syntax), fields);
            structCache[name] = def;
            return def;
        }

        var regions = new List<RegionDef>();
        foreach (var (syntax, path) in regionSyntax)
        {
            var regionAttr = syntax.FindAttribute("ModbusRegion")!;
            var args = AttributeHelpers.ResolveArguments(regionAttr, "title");
            var title = AttributeHelpers.GetStringArgument(args, "title");
            var regionName = syntax.Identifier.Text;

            var registers = new List<RegisterDef>();
            foreach (var member in syntax.Members)
            {
                if (member is not FieldDeclarationSyntax fieldDecl || !fieldDecl.HasAttribute("ModbusRegister"))
                {
                    continue;
                }
                foreach (var declarator in fieldDecl.Declaration.Variables)
                {
                    registers.Add(ResolveRegister(fieldDecl, declarator, regionName, path, ResolveBitfield, ResolveStruct));
                }
            }
            regions.Add(new RegionDef(regionName, title, registers));
        }

        return regions;
    }

    private static SyntaxTree ParseFile(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new SchemaException($"UniversalRegisterAccess: Syntaxfehler in {path}:\n" + string.Join("\n", errors.Select(d => $"  {d}")));
        }
        return tree;
    }

    private static void CollectDeclarations(
        MemberDeclarationSyntax member,
        string path,
        Dictionary<string, EnumDeclarationSyntax> bitfields,
        Dictionary<string, StructDeclarationSyntax> structs,
        List<(ClassDeclarationSyntax, string)> regions)
    {
        switch (member)
        {
            case FileScopedNamespaceDeclarationSyntax fsns:
                foreach (var m in fsns.Members) CollectDeclarations(m, path, bitfields, structs, regions);
                break;
            case NamespaceDeclarationSyntax nds:
                foreach (var m in nds.Members) CollectDeclarations(m, path, bitfields, structs, regions);
                break;
            case EnumDeclarationSyntax eds when eds.HasAttribute("ModbusType"):
                if (!bitfields.TryAdd(eds.Identifier.Text, eds))
                {
                    throw new SchemaException($"{path}: [ModbusType]-Enum \"{eds.Identifier.Text}\" ist mehrfach deklariert.");
                }
                break;
            case StructDeclarationSyntax sds when sds.HasAttribute("ModbusType"):
                if (!structs.TryAdd(sds.Identifier.Text, sds))
                {
                    throw new SchemaException($"{path}: [ModbusType]-Struct \"{sds.Identifier.Text}\" ist mehrfach deklariert.");
                }
                break;
            case ClassDeclarationSyntax cds when cds.HasAttribute("ModbusRegion"):
                regions.Add((cds, path));
                break;
        }
    }

    // ushort/short/uint/int only -- the field types a [ModbusType] struct's own fields may use.
    private static (bool IsSigned, string Keyword) ResolveWordFieldType(TypeSyntax type, string structName)
    {
        if (type is PredefinedTypeSyntax { Keyword.Text: "uint" }) return (false, "uint");
        if (type is PredefinedTypeSyntax { Keyword.Text: "int" }) return (true, "int");
        throw new SchemaException($"[ModbusType] struct \"{structName}\": Felder muessen \"uint\" oder \"int\" sein (ein Register-Wort = 16 Bit, ein struct-Feld = 32 Bit/2 Register).");
    }

    private static RegisterDef ResolveRegister(
        FieldDeclarationSyntax fieldDecl,
        VariableDeclaratorSyntax declarator,
        string regionName,
        string path,
        Func<string, string, BitfieldTypeDef> resolveBitfield,
        Func<string, string, StructTypeDef> resolveStruct)
    {
        var name = declarator.Identifier.Text;
        var fieldLabel = $"{path}: Region \"{regionName}\", Feld \"{name}\"";
        var description = DocComments.Extract(fieldDecl);

        var registerAttr = fieldDecl.FindAttribute("ModbusRegister")!;
        var regArgs = AttributeHelpers.ResolveArguments(registerAttr, "access");
        var access = AttributeHelpers.GetAccessArgument(regArgs, "access");
        var unit = AttributeHelpers.GetStringArgument(regArgs, "Unit");
        var gpio = AttributeHelpers.GetStringArgument(regArgs, "Gpio");
        var i2cBus = AttributeHelpers.GetStringArgument(regArgs, "I2cBus");
        var i2cIrqPin = AttributeHelpers.GetStringArgument(regArgs, "I2cIrqPin");
        var min = AttributeHelpers.GetIntArgument(regArgs, "Min");
        var max = AttributeHelpers.GetIntArgument(regArgs, "Max");
        var display = AttributeHelpers.GetStringArgument(regArgs, "Display");

        int? pinnedAddress = null;
        if (fieldDecl.FindAttribute("ModbusMapping") is { } mappingAttr)
        {
            // "Address" (capital) covers both call styles: [ModbusMapping(1)] (positional, binds to
            // the ctor param via this same name) and [ModbusMapping(Address = 1)] (named, binds to
            // the property) -- C# itself requires named-argument keys to match the PROPERTY name,
            // which is capitalized, so the positional fallback name must match it too or the two
            // call styles would land in different dictionary keys.
            var mArgs = AttributeHelpers.ResolveArguments(mappingAttr, "Address");
            pinnedAddress = AttributeHelpers.GetIntArgument(mArgs, "Address")
                ?? throw new SchemaException($"{fieldLabel}: [ModbusMapping] braucht ein \"Address\"-Argument.");
        }

        int? pinnedNodeId = null;
        if (fieldDecl.FindAttribute("OpcUaMapping") is { } nodeAttr)
        {
            var nArgs = AttributeHelpers.ResolveArguments(nodeAttr, "NodeId");
            pinnedNodeId = AttributeHelpers.GetIntArgument(nArgs, "NodeId")
                ?? throw new SchemaException($"{fieldLabel}: [OpcUaMapping] braucht ein \"NodeId\"-Argument.");
        }

        var type = fieldDecl.Declaration.Type;
        var (valueKind, wordCount, bitfield, structDef) = type switch
        {
            PredefinedTypeSyntax { Keyword.Text: "ushort" } => (RegisterValueKind.UInt16, 1, null, null),
            PredefinedTypeSyntax { Keyword.Text: "short" } => (RegisterValueKind.Int16, 1, null, null),
            PredefinedTypeSyntax { Keyword.Text: "uint" } => (RegisterValueKind.UInt32, 2, null, null),
            PredefinedTypeSyntax { Keyword.Text: "int" } => (RegisterValueKind.Int32, 2, null, null),
            IdentifierNameSyntax id => ResolveNamedFieldType(id.Identifier.Text, fieldLabel, resolveBitfield, resolveStruct),
            _ => throw new SchemaException($"{fieldLabel}: nicht unterstuetzter Feldtyp \"{type}\" (gueltig: ushort/short/uint/int, ein [ModbusType]-Enum, oder ein [ModbusType]-Struct)."),
        };

        return new RegisterDef
        {
            Name = name,
            Description = description,
            Access = access,
            ValueKind = valueKind,
            WordCount = wordCount,
            Bitfield = bitfield,
            Struct = structDef,
            PinnedAddress = pinnedAddress,
            PinnedNodeId = pinnedNodeId,
            Unit = unit,
            Gpio = gpio,
            I2cBus = i2cBus,
            I2cIrqPin = i2cIrqPin,
            Min = min == int.MinValue ? null : min,
            Max = max == int.MinValue ? null : max,
            DisplayOverride = display,
        };
    }

    private static (RegisterValueKind, int, BitfieldTypeDef?, StructTypeDef?) ResolveNamedFieldType(
        string typeName, string fieldLabel, Func<string, string, BitfieldTypeDef> resolveBitfield, Func<string, string, StructTypeDef> resolveStruct)
    {
        // Bitfield vs. struct is disambiguated by which registry the name was found in (see
        // CollectDeclarations -- an enum can only land in bitfieldSyntax, a struct only in
        // structSyntax), so trying one and catching is fine here: at most one of the two calls
        // below actually succeeds.
        try
        {
            var bf = resolveBitfield(typeName, fieldLabel);
            return (RegisterValueKind.Bitfield, 1, bf, null);
        }
        catch (SchemaException)
        {
            var st = resolveStruct(typeName, fieldLabel);
            return (RegisterValueKind.Struct, st.WordCount, null, st);
        }
    }

    private static long EvaluateIntLiteral(ExpressionSyntax expr) => expr switch
    {
        LiteralExpressionSyntax { Token.Value: int i } => i,
        LiteralExpressionSyntax { Token.Value: uint u } => u,
        PrefixUnaryExpressionSyntax { OperatorToken.Text: "-" } prefix => -EvaluateIntLiteral(prefix.Operand),
        _ => throw new SchemaException($"UniversalRegisterAccess: unerwarteter Enum-Wert-Ausdruck \"{expr}\" (erwartet ein Ganzzahl-Literal)."),
    };
}
