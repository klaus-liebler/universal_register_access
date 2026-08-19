namespace UniversalRegisterAccess;

/// <summary>Thrown for any schema problem UniversalRegisterAccess itself detects -- both real C# syntax
/// errors (surfaced via Roslyn's free syntax diagnostics) and our own DSL-specific validation (unknown
/// type reference, address collision, ...). Always carries enough context to locate the offending
/// schema declaration without re-deriving it.</summary>
public sealed class SchemaException(string message) : Exception(message);
