using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>CRC-32 (IEEE) of the register map's layout: per register its bank, address, width, name,
/// signedness, bool display and class - what a client that binds variables to addresses depends on
/// (descriptions, units, limits do not count). C++ (register_map::map_hash) and TypeScript
/// (REGISTER_MAP_HASH) carry the same value, so a program built for one map is recognized on a
/// firmware with another.</summary>
public static class MapHash
{
    public static string Canonical(IReadOnlyList<RegionDef> regions) => string.Join(";",
        regions.SelectMany(r => r.Registers)
            .OrderBy(r => r.Access).ThenBy(r => r.Address)
            .Select(r => $"{(r.Access == Access.ReadOnly ? "i" : "h")}{r.Address}:{r.WordCount}:{Naming.ScreamingSnake(r.Name)}:" +
                         $"{(r.IsSigned ? "s" : "u")}{(r.Display == "bool" ? "b" : "")}:{r.Kind.ToString().ToLowerInvariant()}"));

    public static uint Compute(IReadOnlyList<RegionDef> regions) => Crc32(Encoding.UTF8.GetBytes(Canonical(regions)));

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB8_8320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
