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
    ushort InputsStart);

/// <summary>Generates the CoE object dictionary of the register map for rtoos
/// (register_map_ethercat.hpp, rtos/ethercat/object_dictionary.hpp): one object per region and
/// register class, one entry per register, and one fixed PDO per region - joined in the firmware with
/// rtos::ethercat::standard_objects().
///
/// Indexes are stable: the range base plus the Modbus address of the region's first register of that
/// class (pinned addresses do not move when a region is added):
///   inputs 6000h + address (TxPDO 1A00h + address), outputs 7000h + address (RxPDO 1600h + address),
///   parameters 8000h + address, commands 2000h + address (SDO only: an edge, never cyclic)
/// Subindex n = the n-th register of the class in the region (declaration order).</summary>
public static class EtherCatGenerator
{
    public enum Class { Input, Output, Parameter, Command }

    public sealed record Entry(int SubIndex, RegisterDef Register, string DataType, int Bits);
    public sealed record Obj(int Index, Class Class, RegionDef Region, IReadOnlyList<Entry> Entries)
    {
        public string Name => $"{Region.Title ?? Region.Name} ({ClassName(Class)})";
    }
    public sealed record Pdo(int Index, bool Tx, Obj Source);

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
                    if (bits > 255) throw new SchemaException($"EtherCAT: Register \"{r.Name}\" hat {bits} Bit - mehr als ein PDO-Eintrag fasst (255).");
                    return new Entry(i + 1, r, type, bits);
                }).ToList();
                objects.Add(new Obj(Base(cls) + regs.Min(r => r.Address), cls, region, entries));
            }
        }
        var pdos = objects.Where(o => o.Class is Class.Input or Class.Output)
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
        sb.Append($"inline constexpr ec::SlaveLayout layout{{.mailbox_out_start = 0x{options.MailboxOutStart:X4}, .mailbox_out_size = {options.MailboxOutSize}, " +
                  $".mailbox_in_start = 0x{options.MailboxInStart:X4}, .mailbox_in_size = {options.MailboxInSize}, " +
                  $".outputs_start = 0x{options.OutputsStart:X4}, .inputs_start = 0x{options.InputsStart:X4}}};\n\n");

        foreach (var o in objects)
        {
            sb.Append($"inline constexpr std::array<ec::OdEntry, {o.Entries.Count + 1}> entries_{o.Index:x4}{{{{\n");
            sb.Append($"    ec::count_entry({o.Entries.Count}),\n");
            foreach (var e in o.Entries)
            {
                var access = o.Class switch
                {
                    Class.Input => "ec::access::read | ec::access::txpdo",
                    Class.Output => "ec::access::read_write | ec::access::rxpdo",
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
            sb.Append($"inline constexpr std::array<ec::OdEntry, {p.Source.Entries.Count + 1}> entries_{p.Index:x4}{{{{\n");
            sb.Append($"    ec::count_entry({p.Source.Entries.Count}),\n");
            foreach (var e in p.Source.Entries)
            {
                sb.Append($"    ec::OdEntry{{.subindex = {e.SubIndex}, .data_type = ec::dtype::uint32, .bits = 32, " +
                          $".value = ec::pdo_mapping(0x{p.Source.Index:X4}, {e.SubIndex}, {e.Bits}), .name = \"SubIndex {e.SubIndex:D3}\"}},\n");
            }
            sb.Append("}};\n");
        }

        var all = objects.Select(o => (o.Index, Name: o.Name)).Concat(pdos.Select(p => (p.Index, Name: $"{(p.Tx ? "TxPDO" : "RxPDO")} {p.Source.Region.Title ?? p.Source.Region.Name}")))
            .OrderBy(x => x.Index).ToList();
        sb.Append($"\n/// The application's objects (sorted by index).\n");
        sb.Append($"inline constexpr std::array<ec::OdObject, {all.Count}> objects{{{{\n");
        foreach (var (index, name) in all)
        {
            sb.Append($"    ec::OdObject{{.index = 0x{index:X4}, .code = ec::object_record, .name = {Str(name)}, .entries = entries_{index:x4}}},\n");
        }
        sb.Append("}};\n\n");
        sb.Append($"/// Process data of the default assignment (every PDO): RxPDO and TxPDO bytes.\n");
        sb.Append($"inline constexpr std::uint16_t default_outputs_bytes = {pdos.Where(p => !p.Tx).Sum(p => p.Source.Entries.Sum(e => e.Bits)) / 8}, " +
                  $"default_inputs_bytes = {pdos.Where(p => p.Tx).Sum(p => p.Source.Entries.Sum(e => e.Bits)) / 8};\n\n");
        sb.Append("}  // namespace register_map::ethercat\n");
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
