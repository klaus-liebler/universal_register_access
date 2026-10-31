using UniversalRegisterAccess.Generators;
using UniversalRegisterAccess.Model;
using UniversalRegisterAccess.Parsing;

namespace UniversalRegisterAccess;

/// <summary>Public entry point: reads a set of C# schema files and produces the rtoos C++ register
/// header (register_map.hpp), TypeScript (web UI), and the rtoos OPC UA nodes
/// (register_map_opcua.hpp, below an object named <c>opcUaRootName</c>). Callers own all file I/O
/// around this, same split of responsibility as BestBinaryBuffers.SchemaCompiler.</summary>
public static class SchemaCompiler
{
    public static (string RegisterMapCpp, string Ts, string OpcUaCpp) Generate(IReadOnlyList<string> sourceFiles, string opcUaRootName)
    {
        var regions = SchemaParser.Parse(sourceFiles);
        AddressAssignment.Assign(regions);
        return (CppGenerator.Generate(regions), TsGenerator.Generate(regions), OpcUaGenerator.Generate(regions, opcUaRootName));
    }

    /// <summary>Markdown overview of the complete register map (documentation only).</summary>
    public static string GenerateMarkdown(IReadOnlyList<string> sourceFiles)
    {
        var regions = SchemaParser.Parse(sourceFiles);
        AddressAssignment.Assign(regions);
        return MarkdownGenerator.Generate(regions);
    }

    /// <summary>Like <see cref="Generate"/>, but also writes the three results to disk (creating
    /// parent directories as needed). <paramref name="markdownOutputFilePath"/> optionally adds the
    /// Markdown register overview.</summary>
    public static void Compile(IReadOnlyList<string> sourceFiles, string opcUaRootName, string registerMapCppOutputFilePath, string tsOutputFilePath, string opcUaCppOutputFilePath, string? markdownOutputFilePath = null)
    {
        var regions = SchemaParser.Parse(sourceFiles);
        AddressAssignment.Assign(regions);
        WriteFile(registerMapCppOutputFilePath, CppGenerator.Generate(regions));
        WriteFile(tsOutputFilePath, TsGenerator.Generate(regions));
        WriteFile(opcUaCppOutputFilePath, OpcUaGenerator.Generate(regions, opcUaRootName));
        if (markdownOutputFilePath is not null) WriteFile(markdownOutputFilePath, MarkdownGenerator.Generate(regions));
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }
}
