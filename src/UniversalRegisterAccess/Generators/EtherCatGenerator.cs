using System.Security;
using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>The EtherCAT device's identity and memory layout - written into the generated C++ header
/// (the firmware's SII and slave layout) and into the ESI, so both agree by construction.</summary>
public sealed record EtherCatOptions(
    uint VendorId,
    uint ProductCode,
    uint Revision,            // the ESI revision a master matches (not the hardware revision)
    string Name,
    string Group,
    ushort MailboxOutStart,
    ushort MailboxOutSize,
    ushort MailboxInStart,
    ushort MailboxInSize,
    ushort OutputsStart,
    ushort InputsStart,
    byte PdiControl,          // the ESC's PDI type (SII configuration area, ESI ConfigData)
    string TypeName);         // the ESI device type name (no spaces)

/// <summary>Generates the CoE object dictionary of the register map for rtoos
/// (register_map_ethercat.hpp, rtos/ethercat/object_dictionary.hpp): one object per region and
/// register class, one entry per register, and one fixed PDO per region - joined in the firmware with
/// rtos::ethercat::standard_objects().
///
/// Indexes are stable: the range base plus the Modbus address of the region's first register of that
/// class (pinned addresses do not move when a region is added):
///   inputs 6000h + address (TxPDO 1A00h + address), outputs 7000h + address (RxPDO 1600h + address),
///   parameters 8000h + address, commands 2000h + address (SDO only: an edge, never cyclic)
/// Subindex n = the n-th register of the class in the region (declaration order). A struct register
/// (an octet string, e.g. a chip ID) is SDO only: no PDO entry.
///
/// GenerateEsi() writes the EtherCAT Slave Information (ETG.2000) of the same model: identity, mailbox,
/// SyncManagers, FMMUs, every PDO (fixed, assigned by default, the assignment selectable: PdoAssign).</summary>
public static class EtherCatGenerator
{
    public enum Class { Input, Output, Parameter, Command }

    public sealed record Entry(int SubIndex, RegisterDef Register, string DataType, int Bits)
    {
        public bool Mappable => DataType != "octet_string";
    }
    public sealed record Obj(int Index, Class Class, RegionDef Region, IReadOnlyList<Entry> Entries)
    {
        public string Name => $"{Region.Title ?? Region.Name} ({ClassName(Class)})";
    }
    public sealed record Pdo(int Index, bool Tx, Obj Source)
    {
        public IEnumerable<Entry> Entries => Source.Entries.Where(e => e.Mappable);
        public string Name => $"{(Tx ? "TxPDO" : "RxPDO")} {Source.Region.Title ?? Source.Region.Name}";
    }

    public static string ClassName(Class c) => c switch
    {
        Class.Input => "inputs",
        Class.Output => "outputs",
        Class.Parameter => "parameters",
        _ => "commands",
    };

    public static Class ClassOf(RegisterDef r) => r.Access == Access.ReadOnly
        ? Class.Input
        : r.Kind switch { RegisterKind.Parameter => Class.Parameter, RegisterKind.Command => Class.Command, _ => Class.Output };

    private static int Base(Class c) => c switch { Class.Input => 0x6000, Class.Output => 0x7000, Class.Parameter => 0x8000, _ => 0x2000 };

    public static (string DataType, int Bits) TypeOf(RegisterDef r) => r.ValueKind switch
    {
        RegisterValueKind.UInt16 or RegisterValueKind.Bitfield => ("uint16", 16),
        RegisterValueKind.Int16 => ("int16", 16),
        RegisterValueKind.UInt32 => ("uint32", 32),
        RegisterValueKind.Int32 => ("int32", 32),
        _ => ("octet_string", 16 * r.WordCount),
    };

    /// <summary>The objects and PDOs, sorted by index; throws on a collision or a register that cannot
    /// be described (a PDO entry carries at most 255 bits).</summary>
    public static (IReadOnlyList<Obj> Objects, IReadOnlyList<Pdo> Pdos) Model(IReadOnlyList<RegionDef> regions)
    {
        var objects = new List<Obj>();
        foreach (var region in regions)
        {
            foreach (var cls in new[] { Class.Input, Class.Output, Class.Parameter, Class.Command })
            {
                var regs = region.Registers.Where(r => ClassOf(r) == cls).ToList();
                if (regs.Count == 0) continue;
                if (regs.Count > 254) throw new SchemaException($"EtherCAT: Region \"{region.Name}\" hat mehr als 254 Register der Klasse {cls}.");
                var entries = regs.Select((r, i) =>
                {
                    var (type, bits) = TypeOf(r);
                    return new Entry(i + 1, r, type, bits);
                }).ToList();
                objects.Add(new Obj(Base(cls) + regs.Min(r => r.Address), cls, region, entries));
            }
        }
        var pdos = objects.Where(o => o.Class is Class.Input or Class.Output && o.Entries.Any(e => e.Mappable))
            .Select(o => new Pdo((o.Class == Class.Input ? 0x1A00 : 0x1600) + (o.Index & 0x0FFF), o.Class == Class.Input, o))
            .ToList();
        var all = objects.Select(o => o.Index).Concat(pdos.Select(p => p.Index)).ToList();
        var duplicate = all.GroupBy(i => i).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new SchemaException($"EtherCAT: Objektindex 0x{duplicate.Key:X4} doppelt vergeben.");
        if (pdos.Any(p => p.Index >= (p.Tx ? 0x1C00 : 0x1800))) throw new SchemaException("EtherCAT: PDO-Index ausserhalb 1600h-17FFh / 1A00h-1BFFh.");
        return (objects.OrderBy(o => o.Index).ToList(), pdos.OrderBy(p => p.Index).ToList());
    }

    public static string Generate(IReadOnlyList<RegionDef> regions, EtherCatOptions options)
    {
        var (objects, pdos) = Model(regions);
        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.CppText);
        sb.Append("""

            // The CoE object dictionary of the register map (universal_register_access EtherCatGenerator):
            // one object per region and register class, one fixed PDO per region; joined in the firmware with
            // rtos::ethercat::standard_objects(). Index = range base + Modbus address of the region's first
            // register of that class (stable): inputs 6000h (TxPDO 1A00h), outputs 7000h (RxPDO 1600h),
            // parameters 8000h, commands 2000h.
            #pragma once

            #include <array>
            #include <cstdint>

            #include "rtos/ethercat/object_dictionary.hpp"
            #include "rtos/ethercat/slave.hpp"

            namespace register_map::ethercat {

            namespace ec = rtos::ethercat;


            """);
        sb.Append("/// Identity and memory layout (also in the ESI).\n");
        sb.Append($"inline constexpr std::uint32_t vendor_id = 0x{options.VendorId:X8}, product_code = 0x{options.ProductCode:X8}, revision = 0x{options.Revision:X8};\n");
        sb.Append($"inline constexpr char device_name[] = {Str(options.Name)};\n");
        sb.Append($"inline constexpr char group_name[] = {Str(options.Group)};\n");
        sb.Append($"inline constexpr std::uint8_t pdi_control = 0x{options.PdiControl:X2};\n");
        sb.Append($"inline constexpr ec::SlaveLayout layout{{.mailbox_out_start = 0x{options.MailboxOutStart:X4}, .mailbox_out_size = {options.MailboxOutSize}, " +
                  $".mailbox_in_start = 0x{options.MailboxInStart:X4}, .mailbox_in_size = {options.MailboxInSize}, " +
                  $".outputs_start = 0x{options.OutputsStart:X4}, .inputs_start = 0x{options.InputsStart:X4}}};\n\n");

        foreach (var o in objects)
        {
            sb.Append($"inline constexpr std::array<ec::OdEntry, {o.Entries.Count + 1}> entries_{o.Index:x4}{{{{\n");
            sb.Append($"    ec::count_entry({o.Entries.Count}),\n");
            foreach (var e in o.Entries)
            {
                var access = (o.Class, e.Mappable) switch
                {
                    (Class.Input, true) => "ec::access::read | ec::access::txpdo",
                    (Class.Input, false) => "ec::access::read",
                    (Class.Output, true) => "ec::access::read_write | ec::access::rxpdo",
                    _ => "ec::access::read_write",
                };
                var source = o.Class == Class.Input ? "ec::Source::input" : "ec::Source::holding";
                var signed = e.Register.IsSigned ? ", .is_signed = true" : "";
                sb.Append($"    ec::OdEntry{{.subindex = {e.SubIndex}, .data_type = ec::dtype::{e.DataType}, .bits = {e.Bits}, .access = {access}, " +
                          $".source = {source}, .address = {e.Register.Address}, .name = {Str(Naming.ScreamingSnake(e.Register.Name))}{signed}}},\n");
            }
            sb.Append("}};\n");
        }
        foreach (var p in pdos)
        {
            var mapped = p.Entries.ToList();
            sb.Append($"inline constexpr std::array<ec::OdEntry, {mapped.Count + 1}> entries_{p.Index:x4}{{{{\n");
            sb.Append($"    ec::count_entry({mapped.Count}),\n");
            foreach (var (e, k) in mapped.Select((e, k) => (e, k + 1)))
            {
                sb.Append($"    ec::OdEntry{{.subindex = {k}, .data_type = ec::dtype::uint32, .bits = 32, " +
                          $".value = ec::pdo_mapping(0x{p.Source.Index:X4}, {e.SubIndex}, {e.Bits}), .name = \"SubIndex {k:D3}\"}},\n");
            }
            sb.Append("}};\n");
        }

        var all = objects.Select(o => (o.Index, Name: o.Name)).Concat(pdos.Select(p => (p.Index, Name: p.Name)))
            .OrderBy(x => x.Index).ToList();
        sb.Append($"\n/// The application's objects (sorted by index).\n");
        sb.Append($"inline constexpr std::array<ec::OdObject, {all.Count}> objects{{{{\n");
        foreach (var (index, name) in all)
        {
            sb.Append($"    ec::OdObject{{.index = 0x{index:X4}, .code = ec::object_record, .name = {Str(name)}, .entries = entries_{index:x4}}},\n");
        }
        sb.Append("}};\n\n");
        sb.Append($"/// Process data of the default assignment (every PDO): RxPDO and TxPDO bytes.\n");
        sb.Append($"inline constexpr std::uint16_t default_outputs_bytes = {Bytes(pdos, false)}, default_inputs_bytes = {Bytes(pdos, true)};\n\n");
        sb.Append("}  // namespace register_map::ethercat\n");
        return sb.ToString();
    }

    private static int Bytes(IEnumerable<Pdo> pdos, bool tx) => pdos.Where(p => p.Tx == tx).Sum(p => p.Entries.Sum(e => e.Bits)) / 8;

    private static string EsiType(string dataType) => dataType switch
    {
        "uint16" => "UINT",
        "int16" => "INT",
        "uint32" => "UDINT",
        "int32" => "DINT",
        _ => "",
    };

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
    private static string Hex(long v, int digits) => "#x" + v.ToString($"X{digits}");

    /// <summary>The ESI (ETG.2000) of the device: copy it into TwinCAT's Config\Io\EtherCAT.</summary>
    public static string GenerateEsi(IReadOnlyList<RegionDef> regions, EtherCatOptions o)
    {
        var (_, pdos) = Model(regions);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<!-- GENERIERT von UniversalRegisterAccess (EtherCatGenerator): nicht von Hand editieren. -->\n");
        sb.Append("<EtherCATInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" Version=\"1.6\">\n");
        sb.Append($"  <Vendor>\n    <Id>{Hex(o.VendorId, 8)}</Id>\n    <Name>{X(o.Group)}</Name>\n  </Vendor>\n");
        sb.Append("  <Descriptions>\n");
        sb.Append($"    <Groups>\n      <Group>\n        <Type>{X(o.Group)}</Type>\n        <Name>{X(o.Group)}</Name>\n      </Group>\n    </Groups>\n");
        sb.Append("    <Devices>\n");
        sb.Append("      <Device Physics=\"YY\">\n");
        sb.Append($"        <Type ProductCode=\"{Hex(o.ProductCode, 8)}\" RevisionNo=\"{Hex(o.Revision, 8)}\">{X(o.TypeName)}</Type>\n");
        sb.Append($"        <Name>{X(o.Name)}</Name>\n");
        sb.Append($"        <GroupType>{X(o.Group)}</GroupType>\n");
        sb.Append("        <Fmmu>Outputs</Fmmu>\n        <Fmmu>Inputs</Fmmu>\n        <Fmmu>MBoxState</Fmmu>\n");
        sb.Append($"        <Sm MinSize=\"34\" MaxSize=\"{o.MailboxOutSize}\" DefaultSize=\"{o.MailboxOutSize}\" StartAddress=\"{Hex(o.MailboxOutStart, 4)}\" ControlByte=\"#x26\" Enable=\"1\">MBoxOut</Sm>\n");
        sb.Append($"        <Sm MinSize=\"34\" MaxSize=\"{o.MailboxInSize}\" DefaultSize=\"{o.MailboxInSize}\" StartAddress=\"{Hex(o.MailboxInStart, 4)}\" ControlByte=\"#x22\" Enable=\"1\">MBoxIn</Sm>\n");
        sb.Append($"        <Sm DefaultSize=\"{Bytes(pdos, false)}\" StartAddress=\"{Hex(o.OutputsStart, 4)}\" ControlByte=\"#x64\" Enable=\"1\">Outputs</Sm>\n");
        sb.Append($"        <Sm DefaultSize=\"{Bytes(pdos, true)}\" StartAddress=\"{Hex(o.InputsStart, 4)}\" ControlByte=\"#x20\" Enable=\"1\">Inputs</Sm>\n");
        foreach (var p in pdos.OrderBy(p => p.Tx).ThenBy(p => p.Index))
        {
            var tag = p.Tx ? "TxPdo" : "RxPdo";
            sb.Append($"        <{tag} Fixed=\"1\" Sm=\"{(p.Tx ? 3 : 2)}\">\n");
            sb.Append($"          <Index>{Hex(p.Index, 4)}</Index>\n          <Name>{X(p.Name)}</Name>\n");
            foreach (var e in p.Entries)
            {
                sb.Append($"          <Entry>\n            <Index>{Hex(p.Source.Index, 4)}</Index>\n            <SubIndex>{e.SubIndex}</SubIndex>\n" +
                          $"            <BitLen>{e.Bits}</BitLen>\n            <Name>{X(Naming.ScreamingSnake(e.Register.Name))}</Name>\n" +
                          $"            <DataType>{EsiType(e.DataType)}</DataType>\n          </Entry>\n");
            }
            sb.Append($"        </{tag}>\n");
        }
        sb.Append("        <Mailbox DataLinkLayer=\"true\">\n");
        sb.Append("          <CoE SdoInfo=\"true\" PdoAssign=\"true\" PdoConfig=\"false\" CompleteAccess=\"false\" PdoUpload=\"false\"/>\n");
        sb.Append("        </Mailbox>\n");
        // the configuration area's first 7 words (ETG.2010): PDI control, the rest the defaults
        sb.Append($"        <Eeprom>\n          <ByteSize>512</ByteSize>\n          <ConfigData>{o.PdiControl:X2}000000000000000000000000</ConfigData>\n        </Eeprom>\n");
        sb.Append("      </Device>\n    </Devices>\n  </Descriptions>\n</EtherCATInfo>\n");
        return sb.ToString();
    }

    private static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20 || ch > 0x7E) sb.Append('?');  // VISIBLE_STRING: ASCII
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
