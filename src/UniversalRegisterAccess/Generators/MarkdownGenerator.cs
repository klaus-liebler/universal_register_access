using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates a human-readable Markdown overview of the complete register map (one table per
/// region, plus an address-sorted table per Modbus bank and the bit layout of every bitfield type) --
/// documentation only, nothing in the firmware or web UI reads it.</summary>
public static class MarkdownGenerator
{
    public static string Generate(IReadOnlyList<RegionDef> regions)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- ").Append(GeneratedFileHeader.PlainText.ReplaceLineEndings(" ")).Append(" -->\n\n");
        sb.Append("# Modbus-Register\n\n");
        sb.Append("Input-Register (nur lesen): Funktionscode 04. Holding-Register (lesen/schreiben): Funktionscodes 03/06/16. ");
        sb.Append("Adressen sind 0-basiert (PDU-Adresse). Mehrwort-Werte belegen aufeinanderfolgende Register.\n\n");

        var all = regions.SelectMany(r => r.Registers.Select(reg => (Region: r, Reg: reg))).ToList();
        var inputCount = all.Count(x => x.Reg.Access == Access.ReadOnly);
        var holdingCount = all.Count - inputCount;
        sb.Append($"| Bank | Anzahl Register-Definitionen | Adressbereich |\n|---|---|---|\n");
        sb.Append($"| Input | {inputCount} | {Range(all, Access.ReadOnly)} |\n");
        sb.Append($"| Holding | {holdingCount} | {Range(all, Access.ReadWrite)} |\n\n");

        sb.Append("## Nach Gruppe\n\n");
        foreach (var region in regions.Where(r => r.Registers.Count > 0))
        {
            sb.Append($"### {Cell(region.Title ?? region.Name)}\n\n");
            sb.Append("| Bezeichnung | Bank | Adresse | Worte | Typ | Einheit | Min | Max | Anzeige | GPIO/I2C | OPC-UA-NodeId | Beschreibung |\n");
            sb.Append("|---|---|---:|---:|---|---|---:|---:|---|---|---|---|\n");
            foreach (var reg in region.Registers)
            {
                sb.Append($"| `{Naming.ScreamingSnake(reg.Name)}` | {Bank(reg)} | {AddressText(reg)} | {reg.WordCount} | {TypeText(reg)} | {Cell(reg.Unit)} | {reg.Min?.ToString() ?? ""} | {reg.Max?.ToString() ?? ""} | {reg.Display} | {Cell(Hardware(reg))} | ns=1;i={reg.NodeId} | {Cell(reg.Description)} |\n");
            }
            sb.Append('\n');
        }

        foreach (var access in new[] { Access.ReadOnly, Access.ReadWrite })
        {
            var regs = all.Where(x => x.Reg.Access == access).OrderBy(x => x.Reg.Address).ToList();
            if (regs.Count == 0) continue;
            sb.Append(access == Access.ReadOnly ? "## Input-Register nach Adresse (FC04)\n\n" : "## Holding-Register nach Adresse (FC03/06/16)\n\n");
            sb.Append("| Adresse | Worte | Gruppe | Bezeichnung | Typ | Einheit |\n|---:|---:|---|---|---|---|\n");
            foreach (var (region, reg) in regs)
            {
                sb.Append($"| {AddressText(reg)} | {reg.WordCount} | {Cell(region.Title ?? region.Name)} | `{Naming.ScreamingSnake(reg.Name)}` | {TypeText(reg)} | {Cell(reg.Unit)} |\n");
            }
            sb.Append('\n');
        }

        var bitfields = all.Select(x => x.Reg.Bitfield).OfType<BitfieldTypeDef>().DistinctBy(b => b.Name).ToList();
        if (bitfields.Count > 0)
        {
            sb.Append("## Bitfeld-Typen\n\n");
            foreach (var bf in bitfields)
            {
                sb.Append($"### {bf.Name}\n\n");
                if (bf.Description is not null) sb.Append(Cell(bf.Description)).Append("\n\n");
                sb.Append("| Bit | Name |\n|---:|---|\n");
                foreach (var bit in bf.Bits.OrderBy(b => b.Bit)) sb.Append($"| {bit.Bit} | `{bit.Name}` |\n");
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static string Range(IEnumerable<(RegionDef Region, RegisterDef Reg)> all, Access access)
    {
        var regs = all.Where(x => x.Reg.Access == access).Select(x => x.Reg).ToList();
        if (regs.Count == 0) return "-";
        return $"{regs.Min(r => r.Address)}..{regs.Max(r => r.Address + r.WordCount - 1)}";
    }

    private static string Bank(RegisterDef reg) => reg.Access == Access.ReadOnly ? "Input (R)" : "Holding (R/W)";

    private static string AddressText(RegisterDef reg) =>
        reg.WordCount > 1 ? $"{reg.Address}..{reg.Address + reg.WordCount - 1}" : reg.Address.ToString();

    private static string TypeText(RegisterDef reg) => reg.ValueKind switch
    {
        RegisterValueKind.UInt16 => "uint16",
        RegisterValueKind.Int16 => "int16",
        RegisterValueKind.UInt32 => "uint32",
        RegisterValueKind.Int32 => "int32",
        RegisterValueKind.Bitfield => $"Bitfeld ({reg.Bitfield!.Name})",
        RegisterValueKind.Struct => $"Struct ({reg.Struct!.Name})",
        _ => reg.ValueKind.ToString(),
    };

    private static string? Hardware(RegisterDef reg)
    {
        var parts = new List<string>();
        if (reg.Gpio is not null) parts.Add(reg.Gpio);
        if (reg.I2cBus is not null) parts.Add(reg.I2cIrqPin is not null ? $"{reg.I2cBus} (IRQ {reg.I2cIrqPin})" : reg.I2cBus);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // Markdown-Tabellenzelle: Zeilenumbrueche zusammenfassen, Pipes escapen.
    private static string Cell(string? s) =>
        s is null ? "" : string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Replace("|", "\\|");
}
