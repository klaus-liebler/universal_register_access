using UniversalRegisterAccess.Generators;
using UniversalRegisterAccess.Model;
using UniversalRegisterAccess.Parsing;

namespace UniversalRegisterAccess;

/// <summary>Public entry point: reads a set of C# schema files and produces the C++ (Modbus
/// accessors), TypeScript (web UI), and OPC UA address-space source text. Callers own all file I/O
/// around this, same split of responsibility as BestBinaryBuffers.SchemaCompiler.</summary>
public static class SchemaCompiler
{
    public static (string ModbusCpp, string Ts, string OpcUaCpp) Generate(IReadOnlyList<string> sourceFiles)
    {
        var regions = SchemaParser.Parse(sourceFiles);
        AddressAssignment.Assign(regions);
        return (CppGenerator.Generate(regions), TsGenerator.Generate(regions), OpcUaGenerator.Generate(regions));
    }

    /// <summary>Like <see cref="Generate"/>, but also writes the three results to disk (creating
    /// parent directories as needed).</summary>
    public static void Compile(IReadOnlyList<string> sourceFiles, string modbusCppOutputFilePath, string tsOutputFilePath, string opcUaCppOutputFilePath)
    {
        var (cpp, ts, opcua) = Generate(sourceFiles);
        WriteFile(modbusCppOutputFilePath, cpp);
        WriteFile(tsOutputFilePath, ts);
        WriteFile(opcUaCppOutputFilePath, opcua);
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }
}
