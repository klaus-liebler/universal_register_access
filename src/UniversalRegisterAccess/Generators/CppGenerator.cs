using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates the C++ register header for rtoos (register_map.hpp): address constants
/// (register_map::input::/holding::name), the table sizes for rtos::modbus::RegisterBank, and
/// for every register a typed getter (name(bank)) and setter (set_name(bank, value)). 32-bit
/// values and structs are read and written as one block of the bank, so a Modbus or OPC UA
/// request never sees half of an update.</summary>
public static class CppGenerator
{
    public static string Generate(IReadOnlyList<RegionDef> regions)
    {
        CheckUniqueNames(regions);
        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.CppText);
        sb.Append("""

            #pragma once

            #include <array>
            #include <cstddef>
            #include <cstdint>

            namespace register_map {


            """);

        sb.Append("namespace input {\n\n");
        AppendBankConstants(sb, regions, Access.ReadOnly);
        sb.Append("}  // namespace input\n\n");

        sb.Append("namespace holding {\n\n");
        AppendBankConstants(sb, regions, Access.ReadWrite);
        sb.Append("}  // namespace holding\n\n");

        var (inputCount, holdingCount) = Counts(regions);
        sb.Append("/// Table sizes: rtos::modbus::RegisterBank<holding_count, input_count, Guard>.\n");
        sb.Append($"inline constexpr std::size_t input_count = {inputCount};\n");
        sb.Append($"inline constexpr std::size_t holding_count = {holdingCount};\n\n");

        foreach (var s in DistinctStructTypes(regions))
        {
            AppendStructDef(sb, s);
        }

        sb.Append("""
            namespace detail {
            template <bool Input, std::size_t N, class Bank>
            [[nodiscard]] std::array<std::uint16_t, N> read(const Bank& bank, std::uint16_t address) noexcept {
                std::array<std::uint16_t, N> w{};
                if constexpr (Input) {
                    bank.read_inputs(address, w);
                } else {
                    bank.read_holdings(address, w);
                }
                return w;
            }
            template <bool Input, std::size_t N, class Bank>
            void write(Bank& bank, std::uint16_t address, const std::array<std::uint16_t, N>& w) noexcept {
                if constexpr (Input) {
                    bank.set_inputs(address, w);
                } else {
                    bank.set_holdings(address, w);
                }
            }
            [[nodiscard]] constexpr std::uint32_t join(std::uint16_t high, std::uint16_t low) noexcept {
                return (std::uint32_t{high} << 16) | low;
            }
            [[nodiscard]] constexpr std::uint16_t high(std::uint32_t v) noexcept { return static_cast<std::uint16_t>(v >> 16); }
            [[nodiscard]] constexpr std::uint16_t low(std::uint32_t v) noexcept { return static_cast<std::uint16_t>(v); }
            }  // namespace detail


            """);

        foreach (var region in regions.Where(r => r.Registers.Count > 0))
        {
            sb.Append($"// ---- {region.Title ?? region.Name} ----\n\n");
            foreach (var reg in region.Registers)
            {
                AppendAccessors(sb, reg);
            }
        }

        sb.Append("}  // namespace register_map\n");
        return sb.ToString();
    }

    private static void CheckUniqueNames(IReadOnlyList<RegionDef> regions)
    {
        var duplicate = regions.SelectMany(r => r.Registers).GroupBy(r => Naming.Snake(r.Name)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"register name '{duplicate.Key}' is used more than once");
        }
    }

    private static (int InputCount, int HoldingCount) Counts(IReadOnlyList<RegionDef> regions)
    {
        var input = 0;
        var holding = 0;
        foreach (var reg in regions.SelectMany(r => r.Registers))
        {
            var end = reg.Address + reg.WordCount;
            if (reg.Access == Access.ReadOnly) input = Math.Max(input, end);
            else holding = Math.Max(holding, end);
        }
        return (input, holding);
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
                var comment = BuildComment(reg);
                sb.Append($"inline constexpr std::uint16_t {Naming.Snake(reg.Name)} = {reg.Address};{(comment.Length > 0 ? "  // " + comment : "")}\n");
                if (reg.ValueKind == RegisterValueKind.Bitfield && reg.Bitfield is not null)
                {
                    sb.Append($"namespace {Naming.Snake(reg.Name)}_bit {{\n");
                    foreach (var bit in reg.Bitfield.Bits)
                    {
                        sb.Append($"inline constexpr unsigned {Naming.Snake(bit.Name)} = {bit.Bit};\n");
                    }
                    sb.Append("}\n");
                }
            }
            var first = regs.Min(r => r.Address);
            var last = regs.Max(r => r.Address + r.WordCount - 1);
            var name = Naming.Snake(region.Name);
            sb.Append($"inline constexpr std::uint16_t {name}_first = {first}, {name}_last = {last};\n\n");
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
            var cppType = f.IsSigned ? "std::int32_t" : "std::uint32_t";
            sb.Append($"    {cppType} {f.Name};{(f.Description is not null ? "  // " + f.Description : "")}\n");
        }
        sb.Append("};\n\n");
    }

    // --- Accessors ------------------------------------------------------------------------------
    // Setters exist for input registers too: clients cannot write them, but the firmware produces
    // their values (sensor readings, health bits).

    private static void AppendAccessors(StringBuilder sb, RegisterDef reg)
    {
        var isInput = reg.Access == Access.ReadOnly;
        var name = Naming.Snake(reg.Name);
        var addr = $"{(isInput ? "input" : "holding")}::{name}";
        var get = isInput ? "input" : "holding";
        var set = isInput ? "set_input" : "set_holding";
        var block = isInput ? "true" : "false";

        switch (reg.ValueKind)
        {
            case RegisterValueKind.UInt16:
            case RegisterValueKind.Bitfield:
                sb.Append($"template <class Bank>\n[[nodiscard]] std::uint16_t {name}(const Bank& bank) noexcept {{ return bank.{get}({addr}); }}\n");
                sb.Append($"template <class Bank>\nvoid set_{name}(Bank& bank, std::uint16_t value) noexcept {{ bank.{set}({addr}, value); }}\n");
                break;

            case RegisterValueKind.Int16:
                sb.Append($"template <class Bank>\n[[nodiscard]] std::int16_t {name}(const Bank& bank) noexcept {{ return static_cast<std::int16_t>(bank.{get}({addr})); }}\n");
                sb.Append($"template <class Bank>\nvoid set_{name}(Bank& bank, std::int16_t value) noexcept {{ bank.{set}({addr}, static_cast<std::uint16_t>(value)); }}\n");
                break;

            case RegisterValueKind.UInt32:
            case RegisterValueKind.Int32:
                var type = reg.ValueKind == RegisterValueKind.Int32 ? "std::int32_t" : "std::uint32_t";
                sb.Append($"template <class Bank>\n[[nodiscard]] {type} {name}(const Bank& bank) noexcept {{\n");
                sb.Append($"    const auto w = detail::read<{block}, 2>(bank, {addr});\n");
                sb.Append($"    return static_cast<{type}>(detail::join(w[0], w[1]));\n}}\n");
                sb.Append($"template <class Bank>\nvoid set_{name}(Bank& bank, {type} value) noexcept {{\n");
                sb.Append($"    const auto v = static_cast<std::uint32_t>(value);\n");
                sb.Append($"    detail::write<{block}, 2>(bank, {addr}, {{detail::high(v), detail::low(v)}});\n}}\n");
                break;

            case RegisterValueKind.Struct:
                AppendStructAccessors(sb, reg, name, addr, block);
                break;
        }
        sb.Append('\n');
    }

    private static void AppendStructAccessors(StringBuilder sb, RegisterDef reg, string name, string addr, string block)
    {
        var s = reg.Struct!;
        var words = 2 * s.Fields.Count;
        sb.Append($"template <class Bank>\n[[nodiscard]] {s.Name} {name}(const Bank& bank) noexcept {{\n");
        sb.Append($"    const auto w = detail::read<{block}, {words}>(bank, {addr});\n");
        sb.Append($"    return {s.Name}{{\n");
        for (var i = 0; i < s.Fields.Count; i++)
        {
            var cppType = s.Fields[i].IsSigned ? "std::int32_t" : "std::uint32_t";
            sb.Append($"        static_cast<{cppType}>(detail::join(w[{2 * i}], w[{2 * i + 1}])),\n");
        }
        sb.Append("    };\n}\n");

        sb.Append($"template <class Bank>\nvoid set_{name}(Bank& bank, const {s.Name}& value) noexcept {{\n");
        sb.Append($"    detail::write<{block}, {words}>(bank, {addr}, {{\n");
        foreach (var f in s.Fields)
        {
            sb.Append($"        detail::high(static_cast<std::uint32_t>(value.{f.Name})), detail::low(static_cast<std::uint32_t>(value.{f.Name})),\n");
        }
        sb.Append("    });\n}\n");
    }
}
