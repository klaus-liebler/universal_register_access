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
        sb.Append("Adressen sind 0-basiert (PDU-Adresse). Mehrwort-Werte belegen aufeinanderfolgende Register").Append(ByteOrderRef).Append(".\n\n");

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
                sb.Append($"| `{Naming.Snake(reg.Name)}` | {Bank(reg)} | {AddressText(reg)} | {reg.WordCount} | {TypeText(reg)} | {Cell(reg.Unit)} | {reg.Min?.ToString() ?? ""} | {reg.Max?.ToString() ?? ""} | {reg.Display} | {Cell(Hardware(reg))} | ns=1;i={reg.NodeId} | {Cell(reg.Description)} |\n");
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
                sb.Append($"| {AddressText(reg)} | {reg.WordCount} | {Cell(region.Title ?? region.Name)} | `{Naming.Snake(reg.Name)}` | {TypeText(reg)} | {Cell(reg.Unit)} |\n");
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

        AppendByteOrderFootnote(sb, all.Select(x => x.Reg).ToList());

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private const string ByteOrderRef = "[^byteorder]";

    // Muss zu CppGenerator/OpcUaGenerator passen: High-Word auf der niedrigeren Adresse, und der
    // Modbus-Server serialisiert jedes Register High-Byte zuerst (Modbus-Standard).
    private static void AppendByteOrderFootnote(StringBuilder sb, IReadOnlyList<RegisterDef> regs)
    {
        sb.Append(ByteOrderRef).Append(": Byte-Order durchgehend Big-Endian (\"ABCD\"): Jedes Register wird High-Byte zuerst ");
        sb.Append("uebertragen (Modbus-Standard), und bei 32-Bit-Werten liegt das hoeherwertige Wort (High-Word) auf der ");
        sb.Append("niedrigeren Adresse, das niederwertige Wort (Low-Word) auf der Adresse +1. Vorzeichenbehaftete Werte (int32) ");
        sb.Append("sind im Zweierkomplement codiert. Structs bestehen aus 32-Bit-Feldern in Deklarationsreihenfolge, jedes Feld ");
        sb.Append("ebenfalls High-Word zuerst.\n");

        var example = regs.Where(r => r.ValueKind == RegisterValueKind.Int32).OrderBy(r => r.Access).ThenBy(r => r.Address).FirstOrDefault()
            ?? regs.Where(r => r.ValueKind == RegisterValueKind.UInt32).OrderBy(r => r.Access).ThenBy(r => r.Address).FirstOrDefault();
        if (example is null) return;

        var signed = example.ValueKind == RegisterValueKind.Int32;
        var value = signed ? -100000 : 100000;
        var raw = unchecked((uint)value);
        var hi = (ushort)(raw >> 16);
        var lo = (ushort)raw;
        var fc = example.Access == Access.ReadOnly ? "FC04" : "FC03";
        var bank = example.Access == Access.ReadOnly ? "Input" : "Holding";
        var name = Naming.Snake(example.Name);
        var type = signed ? "int32" : "uint32";

        sb.Append('\n');
        sb.Append($"    Beispiel: `{name}` ({type}, {bank}-Register {example.Address}..{example.Address + 1}) mit dem Wert {value} = 0x{raw:X8}");
        sb.Append(signed ? " (Zweierkomplement)" : "").Append(":\n\n");
        sb.Append($"    - Register {example.Address} (High-Word) = 0x{hi:X4}, Register {example.Address + 1} (Low-Word) = 0x{lo:X4}\n");
        sb.Append($"    - Datenbytes in der {fc}-Antwort (ab Startadresse {example.Address}, Anzahl 2): `{hi >> 8:X2} {hi & 0xFF:X2} {lo >> 8:X2} {lo & 0xFF:X2}`\n");
        sb.Append($"    - Zusammensetzen: `(0x{hi:X4} << 16) | 0x{lo:X4}` = 0x{raw:X8}");
        sb.Append(signed ? $", als int32 interpretiert {value}\n" : $" = {value}\n");
        sb.Append("    - Bei Clients mit einstellbarer Wort-Reihenfolge (z. B. \"Word Swap\"/\"CDAB\") also KEINEN Wort-Tausch waehlen.\n");
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
        RegisterValueKind.UInt32 => "uint32" + ByteOrderRef,
        RegisterValueKind.Int32 => "int32" + ByteOrderRef,
        RegisterValueKind.Bitfield => $"Bitfeld ({reg.Bitfield!.Name})",
        RegisterValueKind.Struct => $"Struct ({reg.Struct!.Name}){ByteOrderRef}",
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
