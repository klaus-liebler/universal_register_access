using UniversalRegisterAccess.Model;

namespace UniversalRegisterAccess.Parsing;

/// <summary>Assigns Modbus addresses and OPC UA NodeIds to every RegisterDef, mutating them in place.
/// Two independent Modbus address sequences (Access.ReadOnly -> Input/FC04, Access.ReadWrite ->
/// Holding/FC03-06-16) and one OPC UA NodeId sequence (namespace 1), all in region/declaration order
/// across the regions list as given. A [ModbusMapping]/[OpcUaMapping] pin sets that register's
/// address/nodeId explicitly and pushes the corresponding running counter past it (collision with an
/// earlier auto-assigned or pinned value is an error -- these numbers are the wire contract, silently
/// overlapping two registers would corrupt both).</summary>
public static class AddressAssignment
{
    // OPC UA NodeIds below this are reserved for hand-maintained server infrastructure (Root/Objects/
    // Server/ServerStatus/*Type/builtin DataType nodes, see OpcUaGenerator) -- register-derived nodes
    // start comfortably above that reserved range.
    public const int OpcUaNodeIdRangeStart = 5000;

    public static void Assign(IReadOnlyList<RegionDef> regions)
    {
        var nextInput = 0;
        var nextHolding = 0;
        var nextNodeId = OpcUaNodeIdRangeStart;
        var usedInput = new HashSet<int>();
        var usedHolding = new HashSet<int>();
        var usedNodeIds = new HashSet<int>();

        foreach (var region in regions)
        {
            foreach (var reg in region.Registers)
            {
                var isInput = reg.Access == Access.ReadOnly;
                var used = isInput ? usedInput : usedHolding;
                var bank = isInput ? "Input" : "Holding";

                int address;
                if (reg.PinnedAddress is { } pinned)
                {
                    address = pinned;
                }
                else
                {
                    address = isInput ? nextInput : nextHolding;
                }
                for (var i = 0; i < reg.WordCount; i++)
                {
                    if (!used.Add(address + i))
                    {
                        throw new SchemaException($"Region \"{region.Name}\", Register \"{reg.Name}\": {bank}-Adresse {address + i} ist bereits vergeben (Adress-Kollision -- pruefe [ModbusMapping]-Pins).");
                    }
                }
                reg.Address = address;
                if (isInput) nextInput = Math.Max(nextInput, address + reg.WordCount);
                else nextHolding = Math.Max(nextHolding, address + reg.WordCount);

                int nodeId;
                if (reg.PinnedNodeId is { } pinnedNodeId)
                {
                    nodeId = pinnedNodeId;
                }
                else
                {
                    nodeId = nextNodeId;
                }
                if (!usedNodeIds.Add(nodeId))
                {
                    throw new SchemaException($"Region \"{region.Name}\", Register \"{reg.Name}\": OPC-UA-NodeId {nodeId} ist bereits vergeben (Kollision -- pruefe [OpcUaMapping]-Pins).");
                }
                reg.NodeId = nodeId;
                nextNodeId = Math.Max(nextNodeId, nodeId + 1);
            }
        }
    }
}
