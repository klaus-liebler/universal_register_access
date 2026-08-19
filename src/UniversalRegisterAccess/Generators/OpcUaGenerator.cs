using System.Text;
using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Generators;

/// <summary>Generates a single self-contained header (Core/generated/opcua_registers_generated.hh --
/// same ".hh" extension as the other Core/generated headers, e.g. modbus_registers_generated.hh)
/// providing GeneratedOpcUa::AddressSpaceInstance() -- a real opcua::AddressSpace built from the same
/// register bestand as the Modbus/TS output, replacing the hand-written test address space
/// (opcua_test_address_space.cpp). Carries forward the same NS0 server-infrastructure nodes that file
/// hand-maintained (Root/Objects/ReferenceTypes/*Type/builtin DataTypes/Server status subtree -- a
/// real client needs these to Browse/watchdog regardless of what registers exist) plus, generated: one
/// Object node per region and one Variable node per register, using COMPACT per-base-type shared
/// callbacks (not one function per register) driven by a small per-register RegisterContext.</summary>
public static class OpcUaGenerator
{
    // Register-derived Object nodes (one per region) live just below the register NodeId range
    // (AddressAssignment.OpcUaNodeIdRangeStart) so both ranges are easy to eyeball as "generated,
    // not hand-maintained infrastructure" without overlapping each other.
    private const int RegionNodeIdRangeStart = 4000;

    public static string Generate(IReadOnlyList<RegionDef> regions)
    {
        var nonEmpty = regions.Where(r => r.Registers.Count > 0).ToList();
        var regionNodeIds = nonEmpty.Select((r, i) => (Region: r, NodeId: RegionNodeIdRangeStart + i)).ToList();

        var sb = new StringBuilder();
        sb.Append(GeneratedFileHeader.CppText);
        sb.Append("\n#pragma once\n");
        sb.Append("#include <cstdio>\n#include <string_view>\n\n");
        sb.Append("#include \"opcua/address_space.hpp\"\n#include \"opcua/node_ids.hpp\"\n#include \"modbus_commons.hh\"\n\n");
        sb.Append("namespace GeneratedOpcUa {\n\nusing namespace opcua;\n\n");

        sb.Append("// Set once at startup (after the runtime ModbusRegisterModel exists, see opcua_setup.cpp) --\n");
        sb.Append("// the Node table below is compile-time-fixed (see address_space.hpp), but the register\n");
        sb.Append("// storage it reads/writes through is a heap object created later, so callbacks reach it via\n");
        sb.Append("// this single pointer instead of a per-node runtime object reference.\n");
        sb.Append("inline Modbus::IModbusRegisterModel *g_RegisterModel = nullptr;\n");
        sb.Append("inline void SetRegisterModel(Modbus::IModbusRegisterModel *model) { g_RegisterModel = model; }\n\n");

        sb.Append("// wordCount is only meaningful for ReadHexString (multi-word/struct registers).\n");
        sb.Append("struct RegisterContext { bool isHolding; uint16_t address; uint8_t wordCount; };\n\n");

        AppendCallbacks(sb);

        sb.Append("namespace {\n\n");
        AppendInfraConstants(sb);

        sb.Append("constexpr RegisterContext kContexts[] = {\n");
        foreach (var (region, _) in regionNodeIds)
        {
            foreach (var reg in region.Registers)
            {
                sb.Append($"    {{ {(reg.Access == Access.ReadWrite ? "true" : "false")}, {reg.Address}, {reg.WordCount} }}, // {region.Name}::{reg.Name}\n");
            }
        }
        sb.Append("};\n\n");

        sb.Append("OPCUA_RODATA constexpr Node kNodes[] = {\n");
        AppendInfraNodes(sb);
        var contextIndex = 0;
        foreach (var (region, regionNodeId) in regionNodeIds)
        {
            AppendRegionObjectNode(sb, region, regionNodeId);
            foreach (var reg in region.Registers)
            {
                AppendRegisterVariableNode(sb, reg, contextIndex);
                contextIndex++;
            }
        }
        sb.Append("};\n\n");

        sb.Append("constexpr ReferenceEntry kReferences[] = {\n");
        AppendInfraReferences(sb);
        foreach (var (region, regionNodeId) in regionNodeIds)
        {
            AppendRegionReferences(sb, region, regionNodeId);
        }
        sb.Append("};\n\n");

        sb.Append("static_assert(AllTypedNodesHaveDataType(kNodes), \"see address_space.hpp\");\n");
        sb.Append("static_assert(AllDataTypesResolve(kNodes), \"see address_space.hpp\");\n");
        sb.Append("static_assert(AllReferenceTypesHaveInverseName(kNodes), \"see address_space.hpp\");\n");
        sb.Append("static_assert(AllReferencesResolve(kNodes, kReferences), \"see address_space.hpp\");\n\n");

        sb.Append("} // namespace\n\n");

        sb.Append("inline const AddressSpace &AddressSpaceInstance() {\n");
        sb.Append("    static constexpr AddressSpace instance(kNodes, kReferences);\n");
        sb.Append("    return instance;\n}\n\n");

        sb.Append("} // namespace GeneratedOpcUa\n");
        return sb.ToString();
    }

    // --- Shared per-base-type callbacks -------------------------------------------------------

    private static void AppendCallbacks(StringBuilder sb)
    {
        sb.Append("inline Variant ReadUInt16(void *context) {\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    auto v = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address) : g_RegisterModel->GetInputRegister(ctx->address);\n");
        sb.Append("    return Variant::Of(static_cast<UInt16>(v));\n}\n\n");

        sb.Append("inline bool WriteUInt16(void *context, const Variant &value) {\n");
        sb.Append("    auto *v = value.GetIf<UInt16>();\n    if (!v) return false;\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address, *v);\n    return true;\n}\n\n");

        sb.Append("inline Variant ReadInt16(void *context) {\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    auto v = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address) : g_RegisterModel->GetInputRegister(ctx->address);\n");
        sb.Append("    return Variant::Of(static_cast<Int16>(v));\n}\n\n");

        sb.Append("inline bool WriteInt16(void *context, const Variant &value) {\n");
        sb.Append("    auto *v = value.GetIf<Int16>();\n    if (!v) return false;\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address, static_cast<uint16_t>(*v));\n    return true;\n}\n\n");

        sb.Append("inline Variant ReadUInt32(void *context) {\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    uint16_t hi = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address) : g_RegisterModel->GetInputRegister(ctx->address);\n");
        sb.Append("    uint16_t lo = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address + 1) : g_RegisterModel->GetInputRegister(ctx->address + 1);\n");
        sb.Append("    return Variant::Of(static_cast<UInt32>(((uint32_t)hi << 16) | lo));\n}\n\n");

        sb.Append("inline bool WriteUInt32(void *context, const Variant &value) {\n");
        sb.Append("    auto *v = value.GetIf<UInt32>();\n    if (!v) return false;\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address, (uint16_t)(*v >> 16));\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address + 1, (uint16_t)*v);\n    return true;\n}\n\n");

        sb.Append("inline Variant ReadInt32(void *context) {\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    uint16_t hi = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address) : g_RegisterModel->GetInputRegister(ctx->address);\n");
        sb.Append("    uint16_t lo = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address + 1) : g_RegisterModel->GetInputRegister(ctx->address + 1);\n");
        sb.Append("    return Variant::Of(static_cast<Int32>((int32_t)(((uint32_t)hi << 16) | lo)));\n}\n\n");

        sb.Append("inline bool WriteInt32(void *context, const Variant &value) {\n");
        sb.Append("    auto *v = value.GetIf<Int32>();\n    if (!v) return false;\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    uint32_t raw = (uint32_t)*v;\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address, (uint16_t)(raw >> 16));\n");
        sb.Append("    g_RegisterModel->SetHoldingRegister(ctx->address + 1, (uint16_t)raw);\n    return true;\n}\n\n");

        sb.Append("// Multi-word/struct registers (e.g. a 96-bit chip UID) -- Variant is scalar-only today (see\n");
        sb.Append("// variant.hpp), so exposed as one computed hex string instead, same formatting web/register-\n");
        sb.Append("// panel.ts already uses for these registers on the Modbus side.\n");
        sb.Append("inline Variant ReadHexString(void *context) {\n");
        sb.Append("    auto *ctx = static_cast<const RegisterContext *>(context);\n");
        sb.Append("    static char buf[128];\n    size_t pos = 0;\n");
        sb.Append("    for (uint8_t i = 0; i < ctx->wordCount && pos + 4 < sizeof(buf); i++) {\n");
        sb.Append("        uint16_t v = ctx->isHolding ? g_RegisterModel->GetHoldingRegister(ctx->address + i) : g_RegisterModel->GetInputRegister(ctx->address + i);\n");
        sb.Append("        pos += (size_t)snprintf(buf + pos, sizeof(buf) - pos, \"%04X\", v);\n    }\n");
        sb.Append("    return Variant::Of(String(std::string_view(buf, pos)));\n}\n\n");
    }

    // --- Server infrastructure (Root/Objects/ReferenceTypes/*Type/builtin DataTypes/Server) ---------
    // Same nodes opcua_test_address_space.cpp hand-maintained -- a real client needs these to Browse
    // from i=85 and to watchdog-poll Server.ServerStatus regardless of what registers exist.

    private static void AppendInfraConstants(StringBuilder sb)
    {
        sb.Append("constexpr NodeId kFactoryControlUnitId(1, 1000);\n\n");
    }

    private static void AppendInfraNodes(StringBuilder sb)
    {
        sb.Append("""
                Node{.nodeId = NodeId(0, ns0::RootFolder), .nodeClass = NodeClass::Object,
                     .browseName = QualifiedName(0, "Root"), .displayName = LocalizedText("en-US", "Root")},
                Node{.nodeId = NodeId(0, ns0::ObjectsFolder), .nodeClass = NodeClass::Object,
                     .browseName = QualifiedName(0, "Objects"), .displayName = LocalizedText("en-US", "Objects")},
                Node{.nodeId = kFactoryControlUnitId, .nodeClass = NodeClass::Object,
                     .browseName = QualifiedName(1, "FactoryControlUnit"), .displayName = LocalizedText("en-US", "FactoryControlUnit")},
                Node{.nodeId = NodeId(0, ns0::Organizes), .nodeClass = NodeClass::ReferenceType,
                     .browseName = QualifiedName(0, "Organizes"), .displayName = LocalizedText("en-US", "Organizes"),
                     .inverseName = LocalizedText("en-US", "OrganizedBy")},
                Node{.nodeId = NodeId(0, ns0::HasComponent), .nodeClass = NodeClass::ReferenceType,
                     .browseName = QualifiedName(0, "HasComponent"), .displayName = LocalizedText("en-US", "HasComponent"),
                     .inverseName = LocalizedText("en-US", "ComponentOf")},
                Node{.nodeId = NodeId(0, ns0::HasTypeDefinition), .nodeClass = NodeClass::ReferenceType,
                     .browseName = QualifiedName(0, "HasTypeDefinition"), .displayName = LocalizedText("en-US", "HasTypeDefinition"),
                     .inverseName = LocalizedText("en-US", "TypeDefinitionOf")},
                Node{.nodeId = NodeId(0, ns0::FolderType), .nodeClass = NodeClass::ObjectType,
                     .browseName = QualifiedName(0, "FolderType"), .displayName = LocalizedText("en-US", "FolderType")},
                Node{.nodeId = NodeId(0, ns0::BaseDataVariableType), .nodeClass = NodeClass::VariableType,
                     .browseName = QualifiedName(0, "BaseDataVariableType"), .displayName = LocalizedText("en-US", "BaseDataVariableType"),
                     .dataType = NodeId(0, ns0::DataType_BaseDataType)},
                Node{.nodeId = NodeId(0, ns0::ServerType), .nodeClass = NodeClass::ObjectType,
                     .browseName = QualifiedName(0, "ServerType"), .displayName = LocalizedText("en-US", "ServerType")},
                Node{.nodeId = NodeId(0, ns0::ServerStatusType), .nodeClass = NodeClass::VariableType,
                     .browseName = QualifiedName(0, "ServerStatusType"), .displayName = LocalizedText("en-US", "ServerStatusType"),
                     .dataType = NodeId(0, ns0::ServerStatusDataType)},
                Node{.nodeId = NodeId(0, ns0::ServerStatusDataType), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "ServerStatusDataType"), .displayName = LocalizedText("en-US", "ServerStatusDataType")},
                Node{.nodeId = NodeId(0, ns0::DataType_Int16), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "Int16"), .displayName = LocalizedText("en-US", "Int16")},
                Node{.nodeId = NodeId(0, ns0::DataType_UInt16), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "UInt16"), .displayName = LocalizedText("en-US", "UInt16")},
                Node{.nodeId = NodeId(0, ns0::DataType_Int32), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "Int32"), .displayName = LocalizedText("en-US", "Int32")},
                Node{.nodeId = NodeId(0, ns0::DataType_UInt32), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "UInt32"), .displayName = LocalizedText("en-US", "UInt32")},
                Node{.nodeId = NodeId(0, ns0::DataType_String), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "String"), .displayName = LocalizedText("en-US", "String")},
                Node{.nodeId = NodeId(0, ns0::DataType_LocalizedText), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "LocalizedText"), .displayName = LocalizedText("en-US", "LocalizedText")},
                Node{.nodeId = NodeId(0, ns0::DataType_BaseDataType), .nodeClass = NodeClass::DataType,
                     .browseName = QualifiedName(0, "BaseDataType"), .displayName = LocalizedText("en-US", "BaseDataType"), .isAbstract = true},
                Node{.nodeId = NodeId(0, ns0::Server), .nodeClass = NodeClass::Object,
                     .browseName = QualifiedName(0, "Server"), .displayName = LocalizedText("en-US", "Server")},
                Node{.nodeId = NodeId(0, ns0::Server_ServerStatus), .nodeClass = NodeClass::Variable,
                     .browseName = QualifiedName(0, "ServerStatus"), .displayName = LocalizedText("en-US", "ServerStatus"),
                     .dataType = NodeId(0, ns0::ServerStatusDataType), .accessLevel = ns0::ACCESS_LEVEL_CURRENT_READ},
                Node{.nodeId = NodeId(0, ns0::Server_ServerStatus_State), .nodeClass = NodeClass::Variable,
                     .browseName = QualifiedName(0, "State"), .displayName = LocalizedText("en-US", "State"),
                     .dataType = NodeId(0, ns0::DataType_Int32), .accessLevel = ns0::ACCESS_LEVEL_CURRENT_READ,
                     .staticValue = Variant::Of(static_cast<Int32>(ns0::ServerState_Running))},
                Node{.nodeId = NodeId(0, ns0::Server_ServerStatus_SecondsTillShutdown), .nodeClass = NodeClass::Variable,
                     .browseName = QualifiedName(0, "SecondsTillShutdown"), .displayName = LocalizedText("en-US", "SecondsTillShutdown"),
                     .dataType = NodeId(0, ns0::DataType_UInt32), .accessLevel = ns0::ACCESS_LEVEL_CURRENT_READ,
                     .staticValue = Variant::Of(static_cast<UInt32>(0))},
                Node{.nodeId = NodeId(0, ns0::Server_ServerStatus_ShutdownReason), .nodeClass = NodeClass::Variable,
                     .browseName = QualifiedName(0, "ShutdownReason"), .displayName = LocalizedText("en-US", "ShutdownReason"),
                     .dataType = NodeId(0, ns0::DataType_LocalizedText), .accessLevel = ns0::ACCESS_LEVEL_CURRENT_READ,
                     .staticValue = Variant::Of(LocalizedText("en-US", ""))},

            """);
    }

    private static void AppendInfraReferences(StringBuilder sb)
    {
        sb.Append("""
                {NodeId(0, ns0::RootFolder), NodeId(0, ns0::Organizes), true, NodeId(0, ns0::ObjectsFolder)},
                {NodeId(0, ns0::ObjectsFolder), NodeId(0, ns0::Organizes), false, NodeId(0, ns0::RootFolder)},
                {NodeId(0, ns0::RootFolder), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::FolderType)},
                {NodeId(0, ns0::ObjectsFolder), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::FolderType)},

                {NodeId(0, ns0::ObjectsFolder), NodeId(0, ns0::Organizes), true, kFactoryControlUnitId},
                {kFactoryControlUnitId, NodeId(0, ns0::Organizes), false, NodeId(0, ns0::ObjectsFolder)},
                {kFactoryControlUnitId, NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::FolderType)},

                {NodeId(0, ns0::ObjectsFolder), NodeId(0, ns0::Organizes), true, NodeId(0, ns0::Server)},
                {NodeId(0, ns0::Server), NodeId(0, ns0::Organizes), false, NodeId(0, ns0::ObjectsFolder)},
                {NodeId(0, ns0::Server), NodeId(0, ns0::HasComponent), true, NodeId(0, ns0::Server_ServerStatus)},
                {NodeId(0, ns0::Server_ServerStatus), NodeId(0, ns0::HasComponent), false, NodeId(0, ns0::Server)},
                {NodeId(0, ns0::Server_ServerStatus), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::ServerStatusType)},
                {NodeId(0, ns0::Server_ServerStatus), NodeId(0, ns0::HasComponent), true, NodeId(0, ns0::Server_ServerStatus_State)},
                {NodeId(0, ns0::Server_ServerStatus_State), NodeId(0, ns0::HasComponent), false, NodeId(0, ns0::Server_ServerStatus)},
                {NodeId(0, ns0::Server_ServerStatus_State), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::BaseDataVariableType)},
                {NodeId(0, ns0::Server_ServerStatus), NodeId(0, ns0::HasComponent), true, NodeId(0, ns0::Server_ServerStatus_SecondsTillShutdown)},
                {NodeId(0, ns0::Server_ServerStatus_SecondsTillShutdown), NodeId(0, ns0::HasComponent), false, NodeId(0, ns0::Server_ServerStatus)},
                {NodeId(0, ns0::Server_ServerStatus_SecondsTillShutdown), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::BaseDataVariableType)},
                {NodeId(0, ns0::Server_ServerStatus), NodeId(0, ns0::HasComponent), true, NodeId(0, ns0::Server_ServerStatus_ShutdownReason)},
                {NodeId(0, ns0::Server_ServerStatus_ShutdownReason), NodeId(0, ns0::HasComponent), false, NodeId(0, ns0::Server_ServerStatus)},
                {NodeId(0, ns0::Server_ServerStatus_ShutdownReason), NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::BaseDataVariableType)},

            """);
    }

    // --- Generated register nodes -------------------------------------------------------------

    private static void AppendRegionObjectNode(StringBuilder sb, RegionDef region, int regionNodeId)
    {
        var title = region.Title ?? region.Name;
        sb.Append($"    Node{{.nodeId = NodeId(1, {regionNodeId}), .nodeClass = NodeClass::Object,\n");
        sb.Append($"         .browseName = QualifiedName(1, \"{region.Name}\"), .displayName = LocalizedText(\"en-US\", \"{title}\")}},\n");
    }

    private static void AppendRegisterVariableNode(StringBuilder sb, RegisterDef reg, int contextIndex)
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
        var isWritable = reg.Access == Access.ReadWrite && writeFn is not null;
        var accessLevel = isWritable ? "ns0::ACCESS_LEVEL_CURRENT_READ | ns0::ACCESS_LEVEL_CURRENT_WRITE" : "ns0::ACCESS_LEVEL_CURRENT_READ";
        var nodeId = $"NodeId(1, {reg.NodeId})";
        var contextExpr = $"const_cast<void *>(static_cast<const void *>(&kContexts[{contextIndex}]))";

        sb.Append($"    Node{{.nodeId = {nodeId}, .nodeClass = NodeClass::Variable,\n");
        sb.Append($"         .browseName = QualifiedName(1, \"{reg.Name}\"), .displayName = LocalizedText(\"en-US\", \"{reg.Name}\"),\n");
        sb.Append($"         .dataType = NodeId(0, ns0::{dataType}), .accessLevel = {accessLevel},\n");
        sb.Append($"         .readCallback = &{readFn},");
        if (isWritable) sb.Append($" .writeCallback = &{writeFn},");
        sb.Append($" .userContext = {contextExpr}}},\n");
    }

    private static void AppendRegionReferences(StringBuilder sb, RegionDef region, int regionNodeId)
    {
        var regionId = $"NodeId(1, {regionNodeId})";
        sb.Append($"    {{kFactoryControlUnitId, NodeId(0, ns0::HasComponent), true, {regionId}}},\n");
        sb.Append($"    {{{regionId}, NodeId(0, ns0::HasComponent), false, kFactoryControlUnitId}},\n");
        sb.Append($"    {{{regionId}, NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::FolderType)}},\n");
        foreach (var reg in region.Registers)
        {
            var regId = $"NodeId(1, {reg.NodeId})";
            sb.Append($"    {{{regionId}, NodeId(0, ns0::HasComponent), true, {regId}}},\n");
            sb.Append($"    {{{regId}, NodeId(0, ns0::HasComponent), false, {regionId}}},\n");
            sb.Append($"    {{{regId}, NodeId(0, ns0::HasTypeDefinition), true, NodeId(0, ns0::BaseDataVariableType)}},\n");
        }
        sb.Append('\n');
    }
}
