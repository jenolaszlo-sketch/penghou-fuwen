using System.Text;
using System.Text.RegularExpressions;

namespace Penghou.Fuwen;

/// <summary>Constructs and validates source-stable structural node paths.</summary>
public static partial class StructuralNodeIdentity
{
    /// <summary>The maximum number of ASCII characters in a source identity segment.</summary>
    public const int MaximumSegmentCharacters = 64;
    /// <summary>The maximum encoded length of a complete structural path.</summary>
    public const int MaximumPathUtf8Bytes = 1024;

    /// <summary>Creates a validated path from a workflow name and named lexical scopes.</summary>
    public static string Create(string workflowName, params string[] lexicalSegments)
    {
        ArgumentNullException.ThrowIfNull(lexicalSegments);
        var segments = new[] { workflowName }.Concat(lexicalSegments).ToArray();
        foreach (var segment in segments)
            ValidateSegment(segment);

        var path = string.Join('/', segments);
        if (Encoding.UTF8.GetByteCount(path) > MaximumPathUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(lexicalSegments), $"Structural path exceeds {MaximumPathUtf8Bytes} UTF-8 bytes.");
        return path;
    }

    /// <summary>Rejects names that cannot be source-stable v1 identity segments.</summary>
    public static void ValidateSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        if (segment.Length > MaximumSegmentCharacters || !IdentifierPattern().IsMatch(segment))
            throw new ArgumentException($"'{segment}' is not a valid Fuwen identity segment.", nameof(segment));
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
