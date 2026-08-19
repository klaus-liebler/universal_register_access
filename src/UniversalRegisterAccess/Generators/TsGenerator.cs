using System.Text;
using System.Text.Json;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates web/generated/register-map.ts. Reproduces the EXACT SAME RegisterDef/
/// RegisterRegion shape the hand-written register-map.json pipeline already produced (name/address/
/// bank/display/description/unit/signed/gpio/i2c/min/max/combine.count) -- register-panel.ts's
/// generic combine+display formatting logic already covers every case this schema can express, so it
/// needs NO changes for this migration.</summary>
public static class TsGenerator
{
    public static string Generate(IReadOnlyList<RegionDef> regions)
    {
        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.TsText);
        sb.Append("\n\n");
        sb.Append("export type RegisterBank = \"input\" | \"holding\";\n");
        sb.Append("export type RegisterDisplay = \"decimal\" | \"hex\" | \"binary\" | \"bool\" | \"bool-error\" | \"status\" | \"range\";\n\n");
        sb.Append("export interface RegisterDef {\n");
        sb.Append("    name: string;\n    address: number;\n    bank: RegisterBank;\n    display: RegisterDisplay;\n");
        sb.Append("    description?: string;\n    unit?: string;\n    signed?: boolean;\n    gpio?: string;\n");
        sb.Append("    i2c?: { bus: string; irqPin?: string };\n    min?: number;\n    max?: number;\n");
        sb.Append("    combine?: { count: number };\n}\n\n");
        sb.Append("export interface RegisterRegion {\n    title: string;\n    registers: RegisterDef[];\n}\n\n");

        var nonEmptyRegions = regions.Where(r => r.Registers.Count > 0).ToList();
        sb.Append("export const REGIONS: RegisterRegion[] = [\n");
        sb.Append(string.Join(",\n", nonEmptyRegions.Select(GenRegion)));
        sb.Append("\n];\n\n");

        var inputMax = MaxAddressExclusive(regions, Access.ReadOnly);
        var holdingMax = MaxAddressExclusive(regions, Access.ReadWrite);
        sb.Append($"export const INPUT_REGISTER_COUNT = {inputMax};\n");
        sb.Append($"export const HOLDING_REGISTER_COUNT = {holdingMax};\n");
        return sb.ToString();
    }

    private static int MaxAddressExclusive(IReadOnlyList<RegionDef> regions, Access access)
    {
        var max = -1;
        foreach (var reg in regions.SelectMany(r => r.Registers).Where(r => r.Access == access))
        {
            max = Math.Max(max, reg.Address + reg.WordCount - 1);
        }
        return max + 1;
    }

    private static string GenRegion(RegionDef region)
    {
        var regsSource = string.Join(",\n", region.Registers.Select(reg => "\t\t\t" + GenRegister(reg)));
        return $"\t{{\n\t\ttitle: {Str(region.Title ?? region.Name)},\n\t\tregisters: [\n{regsSource}\n\t\t]\n\t}}";
    }

    private static string GenRegister(RegisterDef reg)
    {
        var fields = new List<string>
        {
            $"name: {Str(Naming.ScreamingSnake(reg.Name))}",
            $"address: {reg.Address}",
            $"bank: {Str(reg.Access == Access.ReadOnly ? "input" : "holding")}",
        };
        if (reg.Description is not null) fields.Add($"description: {Str(reg.Description)}");
        if (reg.Unit is not null) fields.Add($"unit: {Str(reg.Unit)}");
        if (reg.Gpio is not null) fields.Add($"gpio: {Str(reg.Gpio)}");
        if (reg.IsSigned) fields.Add("signed: true");
        if (reg.I2cBus is not null)
        {
            var i2cFields = new List<string> { $"bus: {Str(reg.I2cBus)}" };
            if (reg.I2cIrqPin is not null) i2cFields.Add($"irqPin: {Str(reg.I2cIrqPin)}");
            fields.Add($"i2c: {{ {string.Join(", ", i2cFields)} }}");
        }
        if (reg.Min is not null) fields.Add($"min: {reg.Min}");
        if (reg.Max is not null) fields.Add($"max: {reg.Max}");
        fields.Add($"display: {Str(reg.Display)}");
        if (reg.WordCount > 1) fields.Add($"combine: {{ count: {reg.WordCount} }}");
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string Str(string s) => JsonSerializer.Serialize(s);
}
