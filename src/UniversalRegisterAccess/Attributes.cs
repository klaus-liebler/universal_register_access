namespace UniversalRegisterAccess;

// These attributes are recognized by SchemaParser purely by NAME (syntax-only Roslyn parsing, no
// semantic model/compilation, same rationale as BestBinaryBuffers) -- referencing this assembly from a
// schema project is optional and only buys IntelliSense/red-squiggles while authoring.

public enum Access { ReadOnly, ReadWrite }

/// <summary>Marks a class as one region (a named group of registers). Registers inside get consecutive
/// addresses (per Access -- ReadOnly/Input and ReadWrite/Holding are independent address sequences)
/// following declaration order across ALL region files, unless pinned via <see cref="ModbusMappingAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ModbusRegionAttribute(string? title = null) : Attribute
{
    public string? Title { get; } = title;
}

/// <summary>Marks a field as one register (or, for a multi-word field type, a contiguous run of
/// registers). ReadOnly -> Modbus Input-Register (FC04); ReadWrite -> Holding-Register (FC03/06/16).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ModbusRegisterAttribute(Access access) : Attribute
{
    public Access Access { get; } = access;
    public string? Unit { get; init; }
    public string? Gpio { get; init; }
    public string? I2cBus { get; init; }
    public string? I2cIrqPin { get; init; }
    // int.MinValue is the "unset" sentinel -- Min/Max are always small non-negative register values in
    // this domain, so it can never collide with a real value (int? is not a legal attribute parameter type).
    public int Min { get; init; } = int.MinValue;
    public int Max { get; init; } = int.MinValue;
    // Overrides the auto-picked web-UI display kind ("decimal"/"hex"/"binary"/"bool"/"bool-error"/
    // "status"/"range"); default is "decimal", except a [ModbusType] bitfield-enum field which defaults
    // to "binary".
    public string? Display { get; init; }
}

/// <summary>Pins a fixed, explicit Modbus address instead of letting it be auto-assigned from
/// declaration order -- the numeric-constant pinning mechanism, no persisted address-map file.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ModbusMappingAttribute(int address) : Attribute
{
    public int Address { get; } = address;
}

/// <summary>Pins a fixed OPC UA NodeId (namespace 1) for this register's generated Variable node,
/// instead of letting it be auto-assigned. Every register gets an OPC UA node by default (in addition
/// to its Modbus exposure) whether or not this attribute is present.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class OpcUaMappingAttribute(int nodeId) : Attribute
{
    public int NodeId { get; } = nodeId;
}

/// <summary>Marks a reusable field type: either a `ushort`-backed enum (documents named bit positions
/// of a 1-register bitfield, e.g. HealthBits -- the register's raw value is NOT interpreted as this
/// enum, it stays a plain bitmask) or a struct of `uint`/`int` fields (a fixed-size multi-word value
/// with no matching built-in primitive size, e.g. a 96-bit chip UID as three uint words).</summary>
[AttributeUsage(AttributeTargets.Enum | AttributeTargets.Struct)]
public sealed class ModbusTypeAttribute : Attribute;
