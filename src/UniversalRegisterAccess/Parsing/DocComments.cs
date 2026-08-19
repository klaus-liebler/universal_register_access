using System.Net;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UniversalRegisterAccess.Parsing;

internal static class DocComments
{
    private static readonly Regex XmlTagRegex = new("<[^>]*>", RegexOptions.Compiled);

    // Extracts the "///" doc-comment directly above a declaration as its Description (used verbatim in
    // generated comments) -- crude XML-tag stripping, good enough for the short one-line summaries
    // actually used in schema files. Same convention as BestBinaryBuffers.AttributeHelpers.ExtractDescription.
    public static string? Extract(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (doc is null) return null;

        var raw = XmlTagRegex.Replace(doc.Content.ToFullString(), " ");
        var lines = raw.Split('\n')
            .Select(l => l.Trim().TrimStart('/').Trim())
            .Where(l => l.Length > 0);
        var text = WebUtility.HtmlDecode(string.Join(" ", lines).Trim());
        return text.Length == 0 ? null : text;
    }
}
