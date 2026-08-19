using UniversalRegisterAccess;

[ModbusType]
public enum HealthBits : ushort
{
    Ok = 0,
    Overtemperature = 1,
    PowerFault = 2,
}

/// <summary>96-Bit Chip-UID.</summary>
[ModbusType]
public struct Uid96
{
    public uint Word0;
    public uint Word1;
    public uint Word2;
}

[ModbusRegion("Diagnostik")]
public class Diagnostics
{
    /// <summary>Health-Bitfeld (siehe HealthBits)</summary>
    [ModbusRegister(Access.ReadOnly)]
    public HealthBits HealthState;

    /// <summary>96-Bit Chip-UID</summary>
    [ModbusRegister(Access.ReadOnly, Display = "hex"), ModbusMapping(Address = 1)]
    public Uid96 ChipId;

    /// <summary>Chip-Temperatur</summary>
    [ModbusRegister(Access.ReadOnly, Unit = "C")]
    public short CpuTemperatureC;
}

[ModbusRegion("Stepper-Steuerung")]
public class StepperCtrl
{
    /// <summary>Zielposition Stepper 1</summary>
    [ModbusRegister(Access.ReadWrite)]
    public int Stepper1Target;

    /// <summary>Geschwindigkeit Stepper 1</summary>
    [ModbusRegister(Access.ReadWrite)]
    public ushort Stepper1Speed;
}
