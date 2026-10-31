using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates the OPC UA part of the register map for rtoos (register_map_opcua.hpp):
/// the application nodes below Objects - one root object, one folder per region, one variable
/// per register - and their references. The variables are bound to the Modbus registers through
/// rtos/opcua/registers.hpp. The server infrastructure (Root, Objects, Server, types) comes from
/// rtoos (rtos::opcua::kBaseNodes / kBaseReferences); the firmware joins both:
///
///   constexpr auto nodes = ua::JoinNodes(ua::kBaseNodes, register_map::opcua::nodes);
///   constexpr auto references = ua::JoinReferences(ua::kBaseReferences, register_map::opcua::references);
/// </summary>
public static class OpcUaGenerator
{
    private const int RootNodeId = 1000;
    // Region folders just below the register NodeId range (AddressAssignment.OpcUaNodeIdRangeStart).
    private const int RegionNodeIdRangeStart = 4000;

    public static string Generate(IReadOnlyList<RegionDef> regions, string rootName)
    {
        var nonEmpty = regions.Where(r => r.Registers.Count > 0).ToList();
        var regionNodeIds = nonEmpty.Select((r, i) => (Region: r, NodeId: RegionNodeIdRangeStart + i)).ToList();
        var registers = nonEmpty.SelectMany(r => r.Registers).ToList();

        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.CppText);
        sb.Append("""

            #pragma once

            #include <array>

            #include "rtos/opcua/address_space.hpp"
            #include "rtos/opcua/node_ids.hpp"
            #include "rtos/opcua/registers.hpp"

            namespace register_map::opcua {

            namespace ua = rtos::opcua;
            using rtos::modbus::Table;


            """);
        sb.Append($"inline constexpr ua::NodeId root(1, {RootNodeId});\n\n");

        sb.Append("/// The register behind each variable (userContext of its node).\n");
        sb.Append($"inline constexpr std::array<ua::registers::Register, {registers.Count}> registers{{{{\n");
        foreach (var reg in registers)
        {
            var table = reg.Access == Access.ReadWrite ? "Table::Holding" : "Table::Input";
            sb.Append($"    {{{table}, {reg.Address}, {reg.WordCount}}},  // {reg.Name}\n");
        }
        sb.Append("}};\n\n");

        var nodeCount = 1 + regionNodeIds.Count + registers.Count;
        sb.Append($"inline constexpr std::array<ua::Node, {nodeCount}> nodes{{{{\n");
        sb.Append($"    {{.nodeId = root, .nodeClass = ua::NodeClass::Object,\n");
        sb.Append($"     .browseName = ua::QualifiedName(1, \"{rootName}\"), .displayName = ua::LocalizedText(\"en-US\", \"{rootName}\")}},\n");
        var index = 0;
        foreach (var (region, regionNodeId) in regionNodeIds)
        {
            var title = region.Title ?? region.Name;
            sb.Append($"    {{.nodeId = ua::NodeId(1, {regionNodeId}), .nodeClass = ua::NodeClass::Object,\n");
            sb.Append($"     .browseName = ua::QualifiedName(1, \"{region.Name}\"), .displayName = ua::LocalizedText(\"en-US\", \"{title}\")}},\n");
            foreach (var reg in region.Registers)
            {
                AppendVariable(sb, reg, index++);
            }
        }
        sb.Append("}};\n\n");

        var referenceCount = 3 + regionNodeIds.Sum(r => 3 + 3 * r.Region.Registers.Count);
        sb.Append($"inline constexpr std::array<ua::ReferenceEntry, {referenceCount}> references{{{{\n");
        sb.Append("    {ua::NodeId(0, ua::ns0::ObjectsFolder), ua::NodeId(0, ua::ns0::Organizes), true, root},\n");
        sb.Append("    {root, ua::NodeId(0, ua::ns0::Organizes), false, ua::NodeId(0, ua::ns0::ObjectsFolder)},\n");
        sb.Append("    {root, ua::NodeId(0, ua::ns0::HasTypeDefinition), true, ua::NodeId(0, ua::ns0::FolderType)},\n");
        foreach (var (region, regionNodeId) in regionNodeIds)
        {
            var regionId = $"ua::NodeId(1, {regionNodeId})";
            sb.Append($"    {{root, ua::NodeId(0, ua::ns0::HasComponent), true, {regionId}}},\n");
            sb.Append($"    {{{regionId}, ua::NodeId(0, ua::ns0::HasComponent), false, root}},\n");
            sb.Append($"    {{{regionId}, ua::NodeId(0, ua::ns0::HasTypeDefinition), true, ua::NodeId(0, ua::ns0::FolderType)}},\n");
            foreach (var reg in region.Registers)
            {
                var regId = $"ua::NodeId(1, {reg.NodeId})";
                sb.Append($"    {{{regionId}, ua::NodeId(0, ua::ns0::HasComponent), true, {regId}}},\n");
                sb.Append($"    {{{regId}, ua::NodeId(0, ua::ns0::HasComponent), false, {regionId}}},\n");
                sb.Append($"    {{{regId}, ua::NodeId(0, ua::ns0::HasTypeDefinition), true, ua::NodeId(0, ua::ns0::BaseDataVariableType)}},\n");
            }
        }
        sb.Append("}};\n\n");

        sb.Append("}  // namespace register_map::opcua\n");
        return sb.ToString();
    }

    private static void AppendVariable(StringBuilder sb, RegisterDef reg, int index)
    {
        var (dataType, readFn, writeFn) = reg.ValueKind switch
        {
            RegisterValueKind.UInt16 or RegisterValueKind.Bitfield => ("DataType_UInt16", "ReadUInt16", "WriteUInt16"),
            RegisterValueKind.Int16 => ("DataType_Int16", "ReadInt16", "WriteInt16"),
            RegisterValueKind.UInt32 => ("DataType_UInt32", "ReadUInt32", "WriteUInt32"),
            RegisterValueKind.Int32 => ("DataType_Int32", "ReadInt32", "WriteInt32"),
            RegisterValueKind.Struct => ("DataType_String", "ReadHexString", (string?)null),
            _ => throw new InvalidOperationException(),
        };
        var writable = reg.Access == Access.ReadWrite && writeFn is not null;
        var access = writable ? "ua::ns0::ACCESS_LEVEL_CURRENT_READ | ua::ns0::ACCESS_LEVEL_CURRENT_WRITE" : "ua::ns0::ACCESS_LEVEL_CURRENT_READ";

        sb.Append($"    {{.nodeId = ua::NodeId(1, {reg.NodeId}), .nodeClass = ua::NodeClass::Variable,\n");
        sb.Append($"     .browseName = ua::QualifiedName(1, \"{reg.Name}\"), .displayName = ua::LocalizedText(\"en-US\", \"{reg.Name}\"),\n");
        sb.Append($"     .dataType = ua::NodeId(0, ua::ns0::{dataType}), .accessLevel = {access},\n");
        sb.Append($"     .readCallback = &ua::registers::{readFn},");
        if (writable) sb.Append($" .writeCallback = &ua::registers::{writeFn},");
        sb.Append($" .userContext = ua::registers::Context(registers[{index}])}},\n");
    }
}
