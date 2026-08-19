using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates a single self-contained C++ header (Core/generated/modbus_registers_generated.hh
/// in the firmware): raw address constants (ModbusRegisters::Input::/Holding::<NAME>, same flat shape
/// consumers already use directly for single-register access) PLUS, for every register, a typed
/// _Read()/_Write() accessor function (ModbusRegisters::Accessors::<Region>::<Name>_Read/_Write) that
/// does any HI/LO-word combination internally -- the part that used to be hand-copied at every call
/// site, see the register-map redesign plan.</summary>
public static class CppGenerator
{
    public static string Generate(IReadOnlyList<RegionDef> regions)
    {
        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.CppText);
        sb.Append("\n#pragma once\n#include <cstdint>\n#include \"modbus_commons.hh\"\n\nnamespace ModbusRegisters {\n\n");

        sb.Append("namespace Input {\n");
        AppendBankConstants(sb, regions, Access.ReadOnly);
        sb.Append("} // namespace Input\n\n");

        sb.Append("namespace Holding {\n");
        AppendBankConstants(sb, regions, Access.ReadWrite);
        sb.Append("} // namespace Holding\n\n");

        var (inputMax, holdingMax) = MaxIndices(regions);
        sb.Append($"constexpr uint16_t INPUT_REGISTER_MAX_INDEX = {inputMax};\n");
        sb.Append($"constexpr uint16_t HOLDING_REGISTER_MAX_INDEX = {holdingMax};\n\n");

        foreach (var s in DistinctStructTypes(regions))
        {
            AppendStructDef(sb, s);
        }

        sb.Append("namespace Accessors {\n\n");
        foreach (var region in regions)
        {
            if (region.Registers.Count == 0) continue;
            sb.Append($"namespace {region.Name} {{\n\n");
            foreach (var reg in region.Registers)
            {
                AppendAccessors(sb, reg);
            }
            sb.Append($"}} // namespace {region.Name}\n\n");
        }
        sb.Append("} // namespace Accessors\n\n");

        sb.Append("} // namespace ModbusRegisters\n");
        return sb.ToString();
    }

    private static (int InputMax, int HoldingMax) MaxIndices(IReadOnlyList<RegionDef> regions)
    {
        var inputMax = -1;
        var holdingMax = -1;
        foreach (var region in regions)
        {
            foreach (var reg in region.Registers)
            {
                var last = reg.Address + reg.WordCount - 1;
                if (reg.Access == Access.ReadOnly) inputMax = Math.Max(inputMax, last);
                else holdingMax = Math.Max(holdingMax, last);
            }
        }
        return (Math.Max(inputMax, 0), Math.Max(holdingMax, 0));
    }

    private static void AppendBankConstants(StringBuilder sb, IReadOnlyList<RegionDef> regions, Access access)
    {
        foreach (var region in regions)
        {
            var regs = region.Registers.Where(r => r.Access == access).ToList();
            if (regs.Count == 0) continue;
            if (region.Title is not null) sb.Append($"// {region.Title}\n");
            foreach (var reg in regs)
            {
                var name = Naming.ScreamingSnake(reg.Name);
                var comment = BuildComment(reg);
                sb.Append($"constexpr uint16_t {name} = {reg.Address};{(comment.Length > 0 ? " // " + comment : "")}\n");
                if (reg.ValueKind == RegisterValueKind.Bitfield && reg.Bitfield is not null)
                {
                    foreach (var bit in reg.Bitfield.Bits)
                    {
                        sb.Append($"constexpr uint16_t HEALTH_BIT_{Naming.ScreamingSnake(bit.Name)} = {bit.Bit};\n");
                    }
                }
            }
            var start = regs.Min(r => r.Address);
            var end = regs.Max(r => r.Address + r.WordCount - 1);
            sb.Append($"constexpr uint16_t {region.Name.ToUpperInvariant()}_REGION_START = {start};\n");
            sb.Append($"constexpr uint16_t {region.Name.ToUpperInvariant()}_REGION_END   = {end};\n\n");
        }
    }

    private static string BuildComment(RegisterDef reg)
    {
        var parts = new List<string>();
        if (reg.Description is not null) parts.Add(reg.Description);
        if (reg.Unit is not null) parts.Add($"[{reg.Unit}]");
        if (reg.IsSigned) parts.Add("signed");
        if (reg.Gpio is not null) parts.Add(reg.Gpio);
        if (reg.I2cBus is not null) parts.Add(reg.I2cIrqPin is not null ? $"{reg.I2cBus}, IRQ {reg.I2cIrqPin}" : reg.I2cBus);
        return string.Join(" -- ", parts);
    }

    private static IEnumerable<StructTypeDef> DistinctStructTypes(IReadOnlyList<RegionDef> regions) =>
        regions.SelectMany(r => r.Registers).Select(r => r.Struct).Where(s => s is not null).Select(s => s!)
            .GroupBy(s => s.Name).Select(g => g.First());

    private static void AppendStructDef(StringBuilder sb, StructTypeDef s)
    {
        if (s.Description is not null) sb.Append($"// {s.Description}\n");
        sb.Append($"struct {s.Name} {{\n");
        foreach (var f in s.Fields)
        {
            var cppType = f.IsSigned ? "int32_t" : "uint32_t";
            sb.Append($"    {cppType} {f.Name};{(f.Description is not null ? " // " + f.Description : "")}\n");
        }
        sb.Append("};\n\n");
    }

    // --- Accessors ------------------------------------------------------------------------------

    private static void AppendAccessors(StringBuilder sb, RegisterDef reg)
    {
        var bank = reg.Access == Access.ReadOnly ? "Input" : "Holding";
        var addr = $"{bank}::{Naming.ScreamingSnake(reg.Name)}";
        var getFn = reg.Access == Access.ReadOnly ? "GetInputRegister" : "GetHoldingRegister";
        // _Write is always emitted, even for Access.ReadOnly (Input) registers: FC04 read-only
        // only means Modbus/OPC-UA CLIENTS can't write it -- the firmware itself is the one
        // producing those values (e.g. chip UID, health bitfield, sensor readings) and needs an
        // internal write path via SetInputRegister. Client write-exposure is gated separately,
        // by Access, in the OPC UA generator (OpcUaGenerator.AppendRegisterVariableNode) and by
        // which Modbus function codes the server implements for Input vs Holding registers.
        var setFn = reg.Access == Access.ReadOnly ? "SetInputRegister" : "SetHoldingRegister";

        switch (reg.ValueKind)
        {
            case RegisterValueKind.UInt16:
            case RegisterValueKind.Bitfield:
                sb.Append($"inline uint16_t {reg.Name}_Read(const Modbus::IModbusRegisterModel& m) {{ return m.{getFn}({addr}); }}\n");
                sb.Append($"inline void {reg.Name}_Write(Modbus::IModbusRegisterModel& m, uint16_t value) {{ m.{setFn}({addr}, value); }}\n");
                break;

            case RegisterValueKind.Int16:
                sb.Append($"inline int16_t {reg.Name}_Read(const Modbus::IModbusRegisterModel& m) {{ return (int16_t)m.{getFn}({addr}); }}\n");
                sb.Append($"inline void {reg.Name}_Write(Modbus::IModbusRegisterModel& m, int16_t value) {{ m.{setFn}({addr}, (uint16_t)value); }}\n");
                break;

            case RegisterValueKind.UInt32:
                sb.Append($"inline uint32_t {reg.Name}_Read(const Modbus::IModbusRegisterModel& m) {{\n");
                sb.Append($"    return ((uint32_t)m.{getFn}({addr}) << 16) | m.{getFn}({addr} + 1);\n}}\n");
                sb.Append($"inline void {reg.Name}_Write(Modbus::IModbusRegisterModel& m, uint32_t value) {{\n");
                sb.Append($"    m.{setFn}({addr}, (uint16_t)(value >> 16));\n    m.{setFn}({addr} + 1, (uint16_t)value);\n}}\n");
                break;

            case RegisterValueKind.Int32:
                sb.Append($"inline int32_t {reg.Name}_Read(const Modbus::IModbusRegisterModel& m) {{\n");
                sb.Append($"    uint32_t raw = ((uint32_t)m.{getFn}({addr}) << 16) | m.{getFn}({addr} + 1);\n    return (int32_t)raw;\n}}\n");
                sb.Append($"inline void {reg.Name}_Write(Modbus::IModbusRegisterModel& m, int32_t value) {{\n");
                sb.Append($"    uint32_t raw = (uint32_t)value;\n    m.{setFn}({addr}, (uint16_t)(raw >> 16));\n    m.{setFn}({addr} + 1, (uint16_t)raw);\n}}\n");
                break;

            case RegisterValueKind.Struct:
                AppendStructAccessors(sb, reg, addr, getFn, setFn);
                break;
        }
        sb.Append('\n');
    }

    private static void AppendStructAccessors(StringBuilder sb, RegisterDef reg, string addr, string getFn, string setFn)
    {
        var s = reg.Struct!;
        sb.Append($"inline {s.Name} {reg.Name}_Read(const Modbus::IModbusRegisterModel& m) {{\n");
        sb.Append($"    return {s.Name}{{\n");
        for (var i = 0; i < s.Fields.Count; i++)
        {
            var f = s.Fields[i];
            var cppType = f.IsSigned ? "int32_t" : "uint32_t";
            var offset = i * 2;
            var offsetExpr = offset == 0 ? addr : $"{addr} + {offset}";
            var offsetExpr2 = $"{addr} + {offset + 1}";
            sb.Append($"        ({cppType})(((uint32_t)m.{getFn}({offsetExpr}) << 16) | m.{getFn}({offsetExpr2})),\n");
        }
        sb.Append("    };\n}\n");

        sb.Append($"inline void {reg.Name}_Write(Modbus::IModbusRegisterModel& m, const {s.Name}& value) {{\n");
        for (var i = 0; i < s.Fields.Count; i++)
        {
            var f = s.Fields[i];
            var offset = i * 2;
            var offsetExpr = offset == 0 ? addr : $"{addr} + {offset}";
            var offsetExpr2 = $"{addr} + {offset + 1}";
            sb.Append($"    m.{setFn}({offsetExpr}, (uint16_t)((uint32_t)value.{f.Name} >> 16));\n");
            sb.Append($"    m.{setFn}({offsetExpr2}, (uint16_t)value.{f.Name});\n");
        }
        sb.Append("}\n");
    }
}
