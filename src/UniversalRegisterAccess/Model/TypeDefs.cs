namespace UniversalRegisterAccess.Model;

public sealed record BitValue(string Name, int Bit);

// [ModbusType] enum -- documents named bit positions of a 1-register bitfield (e.g. HealthBits). The
// register itself stores a raw uint16 bitmask; this type is metadata only (drives generated
// HEALTH_BIT_*-style constants), never a typed accessor return value.
public sealed record BitfieldTypeDef(string Name, string? Description, IReadOnlyList<BitValue> Bits);

public sealed record StructWordField(string Name, string? Description, bool IsSigned);

// [ModbusType] struct -- a fixed-size multi-word value with no matching built-in primitive size (e.g.
// a 96-bit chip UID). Fields are always uint/int (2 registers each), in declared order.
public sealed record StructTypeDef(string Name, string? Description, IReadOnlyList<StructWordField> Fields)
{
    public int WordCount => Fields.Count * 2;
}

public enum RegisterValueKind { UInt16, Int16, UInt32, Int32, Bitfield, Struct }

// Address/NodeId are assigned by AddressAssignment AFTER parsing (mutable on purpose -- parsing and
// address assignment are deliberately separate passes, mirroring why BestBinaryBuffers keeps "raw
// discovery" and "resolution" separate: cross-region/cross-file ordering can't be known while a single
// file is still being parsed).
public sealed class RegisterDef
{
    public required string Name { get; init; } // C# field identifier, PascalCase (e.g. "ChipId")
    public string? Description { get; init; }
    public required Access Access { get; init; }
    public required RegisterValueKind ValueKind { get; init; }
    public required int WordCount { get; init; }
    public BitfieldTypeDef? Bitfield { get; init; } // set iff ValueKind == Bitfield
    public StructTypeDef? Struct { get; init; }     // set iff ValueKind == Struct
    public int? PinnedAddress { get; init; }
    public int? PinnedNodeId { get; init; }
    public string? Unit { get; init; }
    public string? Gpio { get; init; }
    public string? I2cBus { get; init; }
    public string? I2cIrqPin { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public string? DisplayOverride { get; init; }

    public bool IsSigned => ValueKind is RegisterValueKind.Int16 or RegisterValueKind.Int32;

    // Assigned by AddressAssignment.
    public int Address { get; set; }
    public int NodeId { get; set; }

    public string Display => DisplayOverride ?? (ValueKind == RegisterValueKind.Bitfield ? "binary" : "decimal");
}

public sealed record RegionDef(string Name, string? Title, IReadOnlyList<RegisterDef> Registers);
